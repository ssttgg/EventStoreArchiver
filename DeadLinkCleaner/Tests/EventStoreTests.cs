﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EventStore.ClientAPI;
using EventStore.ClientAPI.SystemData;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DeadLinkCleaner
{
    public class EventStoreTests : EventStoreBase
    {
        private readonly LinkToStreamTruncator _truncator;

        public EventStoreTests(string connectionString)
            : base(connectionString)
        {
            _truncator = new LinkToStreamTruncator(connectionString);
        }

        public async Task RunAll()
        {
            await Scenario1_CompleteTruncationResumesNormalActivity("X", "Y");
            await Scenario2_PersistentSubscriptionIsBehind("Z");
            await Scenario3_CustomProjectionIsBehind("A");
            await Scenario4_NoDeadLinks("B");
        }

        private async Task Scenario4_NoDeadLinks(string categoryName1)
        {
            
            var stream1 = $"{categoryName1}-1";
            var category1 = $"$ce-{categoryName1}";

            var count = 10000;
            
            await GenerateEvents(stream1, count);
            
            // no dead links
            var safePoint1 = await _truncator.FindSafeTruncationBeforePoint(category1);

            if (safePoint1.HasValue)
                throw new Exception();
            
            await SetTruncateBefore(stream1, count + 1);

            
            await Task.Delay(3000);
            
            // all dead links
            var safePoint2 = await _truncator.FindSafeTruncationBeforePoint(category1);

            if (!safePoint2.HasValue || safePoint2 != 10000)
                throw new Exception();

        }

        public async Task Scenario1_CompleteTruncationResumesNormalActivity(string categoryName1, string categoryName2)
        {
            var esc = await EventStoreConnection;

            var stream1 = $"{categoryName1}-1";
            var stream2 = $"{categoryName2}-1";

            var category1 = $"$ce-{categoryName1}";
            var category2 = $"$ce-{categoryName2}";

            var count = 10000;

            await Task.WhenAll(
                GenerateEvents(stream1, count),
                GenerateEvents(stream2, count));

            await Task.Delay(5000);

            var streamLength = await GetStreamLength(stream1);

            if (streamLength != count)
                throw new Exception();

            await SetTruncateBefore(stream1, streamLength + 1);


            var safePoint1 = await _truncator.FindSafeTruncationBeforePoint(category1);

            if (!safePoint1.HasValue || safePoint1 != count)
                throw new Exception();

            var safePoint2 = await _truncator.FindSafeTruncationBeforePoint(category2);

            if (safePoint2.HasValue)
                throw new Exception();


            await SetTruncateBefore(category1, safePoint1.Value + 1);

//            Console.WriteLine("Restart ES now...Press key when done");
//            Console.ReadKey();

            await Task.Delay(1000);

            await GenerateEvents(stream1, count);

            await Task.Delay(5000);

            streamLength = await GetStreamLength(category1);

            if (streamLength < 20000) // 20000 events + metadata
                throw new Exception();
        }

        public async Task Scenario2_PersistentSubscriptionIsBehind(string categoryName1)
        {
            //var esc = await EventStoreConnection;

            var stream1 = $"{categoryName1}-1";

            var category1 = $"$ce-{categoryName1}";

            var count = 10000;

            await GenerateEvents(stream1, count);

            await Task.Delay(1000);

            await ReinitializePersistentSubscription(category1, "Scenario2");

            await Task.Delay(1000);
            
            var persistentSubscriptionReads = count / 4;

            await SubscribeAndRead(category1, "Scenario2", persistentSubscriptionReads); // 2500

            
            // Truncate X-1
            var streamLength = await GetStreamLength(stream1);

            if (streamLength != count)
                throw new Exception();

            await SetTruncateBefore(stream1, streamLength + 1);
            
            await Task.Delay(5000);

            // Now find safe truncate point
            var safePoint1 = await _truncator.FindSafeTruncationBeforePoint(category1);

            // safe point is the persistent subscription checkpoint - 1, when is persistentSubscriptionReads - 2 (as checkpoint is 1 behind number of reads). 
            
            if (!safePoint1.HasValue || safePoint1 != persistentSubscriptionReads - 2)
                throw new Exception();

            await SetTruncateBefore(category1, safePoint1.Value + 1);

            await Task.Delay(5000);
            
//            Console.WriteLine("Restart ES now...Press key when done");
//            Console.ReadKey();
            
            await SubscribeAndRead(category1, "Scenario2", count - persistentSubscriptionReads); // 7500

            await Task.Delay(5000);
            
            // Now find safe truncate point
            var safePoint2 = await _truncator.FindSafeTruncationBeforePoint(category1);

            if (!safePoint2.HasValue || safePoint2 != count - 2)
                throw new Exception();
            
        }

        public async Task Scenario3_CustomProjectionIsBehind(string categoryName1)
        {
            var pm = await ProjectionsManager;

            var name = $"{categoryName1}TestProjection";

            var admin = new UserCredentials("admin", "changeit");
            
            var stream1 = $"{categoryName1}-1";
            var category1 = $"$ce-{categoryName1}";

            await GenerateEvents(stream1, 5000); // default checkpoint is 4000
            
            await pm.CreateContinuousAsync(name, $@"
                fromCategory('{categoryName1}')
                  .when({{
                    ""$init"": function(state, ev) {{
                            return {{ count: 0 }};
                        }},
                       ""$any"": function(state, ev) {{
                            state.count++;
                            return state;
                        }}
                    }})
            ", admin);

            await Task.Delay(5000);
            
            await pm.DisableAsync(name, admin);
            
            await Task.Delay(1000);

            await GenerateEvents(stream1, 5000); // another 5000

            await Task.Delay(1000);
            
            // Truncate X-1
            await SetTruncateBefore(stream1, 10000 + 1);
            
            await Task.Delay(1000);

            // Now find safe truncate point
            var safePoint1 = await _truncator.FindSafeTruncationBeforePoint(category1);
            
            if (!safePoint1.HasValue || safePoint1 != 5000 - 2)
                throw new Exception();
            
            // Re-enable
            await pm.EnableAsync(name, admin);
            
            // wait for projection to catch up
            await Task.Delay(5000);

            // Now find safe truncate point
            var safePoint2 = await _truncator.FindSafeTruncationBeforePoint(category1);
            
            if (!safePoint2.HasValue || safePoint2 != 9000 - 2)  // 9000 is the latest checkpoint for the projection...
                throw new Exception();
            

        }

        private async Task ReinitializePersistentSubscription(string stream, string groupName)
        {
            var esc = await EventStoreConnection;

            try
            {
                await esc.DeletePersistentSubscriptionAsync(stream, groupName);
            }
            catch { }

            await esc.CreatePersistentSubscriptionAsync(stream, groupName,
                PersistentSubscriptionSettings.Create()
                    .StartFromBeginning()
                    .ResolveLinkTos()
                    .Build(),
                null);
        }

        private async Task SubscribeAndRead(string stream, string groupName, int readCount)
        {
            var esc = await EventStoreConnection;

            int processed = 0;

            var s = await esc.ConnectToPersistentSubscriptionAsync(stream, groupName,
                (sub, re) =>
                {
                    if (processed++ == readCount)
                    {
                        sub.Stop(TimeSpan.FromMilliseconds(1000));
                    }

                    return Task.CompletedTask;
                },
                (sub, reason, ex) => { },
                null,
                20,
                true);

            SpinWait.SpinUntil(() => processed >= readCount);

        }

        private async Task SetTruncateBefore(string stream, long truncateBefore)
        {
            var esc = await EventStoreConnection;

            var metadata = await esc.GetStreamMetadataAsync(stream);

            var streamMetadata = metadata.StreamMetadata;

            var edittedStreamMetadata = streamMetadata
                .Copy()
                .SetTruncateBefore(truncateBefore)
                .Build();

            await esc.SetStreamMetadataAsync(stream, ExpectedVersion.Any, edittedStreamMetadata);
        }

        private async Task<long> GetStreamLength(string stream)
        {
            var esc = await EventStoreConnection;

            var read = await esc.ReadStreamEventsBackwardAsync(stream, StreamPosition.End, 1, true);

            if (read.Status == SliceReadStatus.Success)
            {
                return read.LastEventNumber + 1;
            }

            return -1;
        }

        private async Task GenerateEvents(string stream, int count)
        {
            var esc = await EventStoreConnection;

            var metadata = new byte[0];

            var eventDatas = Enumerable.Range(0, count).Select(i => new EventData(Guid.NewGuid(), "TestEventType", true,
                Encoding.UTF8.GetBytes(JObject.FromObject(new {index = i}).ToString()), metadata));

            foreach (var batch in eventDatas.Batch(1000))
            {
                await esc.AppendToStreamAsync(stream, ExpectedVersion.Any, batch);
            }
        }
    }
}