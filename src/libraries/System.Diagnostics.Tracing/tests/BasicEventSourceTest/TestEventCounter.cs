// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics.Tracing;
#if USE_ETW // TODO: Enable when TraceEvent is available on CoreCLR. GitHub issue https://github.com/dotnet/corefx/issues/4864 
using Microsoft.Diagnostics.Tracing.Session;
#endif
using Xunit;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BasicEventSourceTests
{
    public class TestEventCounter
    {
        private sealed class MyEventSource : EventSource
        {
            private EventCounter _requestCounter;
            private EventCounter _errorCounter;

            public MyEventSource()
            {
                _requestCounter = new EventCounter("Request", this);
                _errorCounter = new EventCounter("Error", this);
            }

            public void Request(float elapsed)
            {
                _requestCounter.WriteMetric(elapsed);
            }

            public void Error()
            {
                _errorCounter.WriteMetric(1);
            }
        }

        [Fact]
        public void Test_Write_Metric_EventListener()
        {
            using (var listener = new EventListenerListener())
            {
                Test_Write_Metric(listener);
            }
        }

#if USE_ETW
        [Fact]
        public void Test_Write_Metric_ETW()
        {

            using (var listener = new EtwListener())
            {
                Test_Write_Metric(listener);
            }
        }
#endif

        private void Test_Write_Metric(Listener listener)
        {

            Console.WriteLine("Version of Runtime {0}", Environment.Version);
            Console.WriteLine("Version of OS {0}", Environment.OSVersion);
            TestUtilities.CheckNoEventSourcesRunning("Start");

            using (var logger = new MyEventSource())
            {
                var tests = new List<SubTest>();
                /*************************************************************************/
                tests.Add(new SubTest("Log 1 event, explicit poll at end",
                    delegate ()
                    {
                        listener.EnableTimer(logger, 1);        // Set to poll every second, but we dont actually care because the test ends before that.   
                        logger.Request(5);
                        listener.EnableTimer(logger, 0);
                    },
                    delegate (List<Event> evts)
                    {
                        // There will be two events (request and error) for time 0 and 2 more at 1 second and 2 more when we shut it off.  
                        Assert.Equal(4, evts.Count);
                        ValidateSingleEventCounter(evts[0], "Request", 0, 0, 0, float.PositiveInfinity, float.NegativeInfinity);
                        ValidateSingleEventCounter(evts[1], "Error", 0, 0, 0, float.PositiveInfinity, float.NegativeInfinity);
                        ValidateSingleEventCounter(evts[2], "Request", 1, 5, 0, 5, 5);
                        ValidateSingleEventCounter(evts[3], "Error", 0, 0, 0, float.PositiveInfinity, float.NegativeInfinity);
                    }));
                /*************************************************************************/
                tests.Add(new SubTest("Log 2 events, explicit poll at end",
                    delegate ()
                    {
                        listener.EnableTimer(logger, 1);        // Set to poll every second, but we dont actually care because the test ends before that.   
                        logger.Request(5);
                        logger.Request(10);
                        listener.EnableTimer(logger, 0);        // poll 
                    },
                    delegate (List<Event> evts)
                    {
                        Assert.Equal(4, evts.Count);
                        ValidateSingleEventCounter(evts[0], "Request", 0, 0, 0, float.PositiveInfinity, float.NegativeInfinity);
                        ValidateSingleEventCounter(evts[1], "Error", 0, 0, 0, float.PositiveInfinity, float.NegativeInfinity);
                        ValidateSingleEventCounter(evts[2], "Request", 2, 7.5f, 2.5f, 5, 10);
                        ValidateSingleEventCounter(evts[3], "Error", 0, 0, 0, float.PositiveInfinity, float.NegativeInfinity);
                    }));

                /*************************************************************************/
                tests.Add(new SubTest("Log 3 events in two polling periods (explicit polling)",
                    delegate ()
                    {
                        listener.EnableTimer(logger, 0);  /* Turn off (but also poll once) */
                        logger.Request(5);
                        logger.Request(10);
                        logger.Error();
                        listener.EnableTimer(logger, 0);  /* Turn off (but also poll once) */
                        logger.Request(8);
                        logger.Error();
                        logger.Error();
                        listener.EnableTimer(logger, 0);  /* Turn off (but also poll once) */
                    },
                    delegate (List<Event> evts)
                    {
                        Assert.Equal(6, evts.Count);
                        ValidateSingleEventCounter(evts[0], "Request", 0, 0, 0, float.PositiveInfinity, float.NegativeInfinity);
                        ValidateSingleEventCounter(evts[1], "Error", 0, 0, 0, float.PositiveInfinity, float.NegativeInfinity);
                        ValidateSingleEventCounter(evts[2], "Request", 2, 7.5f, 2.5f, 5, 10);
                        ValidateSingleEventCounter(evts[3], "Error", 1, 1, 0, 1, 1);
                        ValidateSingleEventCounter(evts[4], "Request", 1, 8, 0, 8, 8);
                        ValidateSingleEventCounter(evts[5], "Error", 2, 1, 0, 1, 1);
                    }));


                /*************************************************************************/
                tests.Add(new SubTest("Log multiple events in",
                    delegate ()
                    {
                        listener.EnableTimer(logger, .1); /* Poll every .1 s */
                                                          // logs at 0 seconds because of EnableTimer command
                        Sleep(100);
                        logger.Request(1);
                        Sleep(100);
                        logger.Request(2);
                        logger.Error();
                        Sleep(100);
                        logger.Request(4);
                        Sleep(100);
                        logger.Error();
                        logger.Request(8);
                        Sleep(100);
                        logger.Request(16);
                        Sleep(200);
                        listener.EnableTimer(logger, 0);
                    },
                    delegate (List<Event> evts)
                    {

                        int requestCount = 0;
                        float requestSum = 0;
                        float requestMin = float.MaxValue;
                        float requestMax = float.MinValue;

                        int errorCount = 0;
                        float errorSum = 0;
                        float errorMin = float.MaxValue;
                        float errorMax = float.MinValue;

                        float timeSum = 0;

                        for (int j = 0; j < evts.Count; j += 2)
                        {
                            var requestPayload = ValidateEventHeaderAndGetPayload(evts[j]);
                            Assert.Equal("Request", requestPayload["Name"]);

                            var count = (int)requestPayload["Count"];
                            requestCount += count;
                            if (count > 0)
                                requestSum += (float)requestPayload["Mean"] * count;
                            requestMin = Math.Min(requestMin, (float)requestPayload["Min"]);
                            requestMax = Math.Max(requestMax, (float)requestPayload["Max"]);
                            float requestIntevalSec = (float)requestPayload["IntervalSec"];

                            var errorPayload = ValidateEventHeaderAndGetPayload(evts[j + 1]);
                            Assert.Equal("Error", errorPayload["Name"]);

                            count = (int)errorPayload["Count"];
                            errorCount += count;
                            if (count > 0)
                                errorSum += (float)errorPayload["Mean"] * count;
                            errorMin = Math.Min(errorMin, (float)errorPayload["Min"]);
                            errorMax = Math.Max(errorMax, (float)errorPayload["Max"]);
                            float errorIntevalSec = (float)requestPayload["IntervalSec"];

                            Assert.Equal(requestIntevalSec, errorIntevalSec);
                            timeSum += requestIntevalSec;
                        }
                        Assert.Equal(requestCount, 5);
                        Assert.Equal(requestSum, 31);
                        Assert.Equal(requestMin, 1);
                        Assert.Equal(requestMax, 16);

                        Assert.Equal(errorCount, 2);
                        Assert.Equal(errorSum, 2);
                        Assert.Equal(errorMin, 1);
                        Assert.Equal(errorMax, 1);

                        Assert.True(.4 < timeSum, $"FAILURE: .4 < {timeSum}");  // We should have at least 400 msec 
                        Assert.True(timeSum < 2, $"FAILURE: {timeSum} < 2");    // But well under 2 sec.  

                        // Do all the things that depend on the count of events last so we know everything else is sane 
                        Assert.True(4 <= evts.Count, "We expect two metrices at the begining trigger and two at the end trigger.  evts.Count = " + evts.Count);
                        Assert.True(evts.Count % 2 == 0, "We expect two metrics for every trigger.  evts.Count = " + evts.Count);

                        ValidateSingleEventCounter(evts[0], "Request", 0, 0, 0, float.PositiveInfinity, float.NegativeInfinity);
                        ValidateSingleEventCounter(evts[1], "Error", 0, 0, 0, float.PositiveInfinity, float.NegativeInfinity);

                        // We expect the timer to have gone off at least twice, plus the explicit poll at the begining and end.
                        // Each one fires two events (one for requests, one for errors). so that is (2 + 2)*2 = 8
                        // We expect about 5 timer requests, but we don't get picky about the exact count
                        // We don't expect more than say 9 timer request so that is (2 + 9) * 2 = 22
                        Assert.True(8 <= evts.Count, $"FAILURE: 8 <= {evts.Count}");
                        Assert.True(evts.Count <= 22, $"FAILURE: {evts.Count} <= 22");
                    }));


                /*************************************************************************/
                // TODO expose Dispose() method and activate this test.  
#if EventCounterDispose
                tests.Add(new SubTest("EventCounter.Dispose()",
                    delegate ()
                    {
                        // Creating and destroying 
                        var myCounter = new EventCounter("counter for a transient object", logger);
                        myCounter.WriteMetric(10);
                        listener.EnableTimer(logger, 0);  /* Turn off (but also poll once) */
                        myCounter.Dispose();
                        listener.EnableTimer(logger, 0);  /* Turn off (but also poll once) */
                    },
                    delegate (List<Event> evts)
                    {
                        Assert.Equal(5, evts.Count);
                        ValidateSingleEventCounter(evts[0], "Request", 0, 0, 0, 0, 0);
                        ValidateSingleEventCounter(evts[1], "Error", 0, 0, 0, 0, 0);
                        ValidateSingleEventCounter(evts[2], "counter for a transient object", 1, 10, 0, 10, 10);
                        ValidateSingleEventCounter(evts[3], "Request", 0, 0, 0, 0, 0);
                        ValidateSingleEventCounter(evts[4], "Error", 0, 0, 0, 0, 0);
                    }));
#endif 
                /*************************************************************************/
                EventTestHarness.RunTests(tests, listener, logger);
            }
            TestUtilities.CheckNoEventSourcesRunning("Stop");
        }

        // Thread.Sleep has proven unreliable, sometime sleeping much shorter than it should. 
        // This makes sure it at least sleeps 'msec' at a miniumum.  
        private static void Sleep(int minMSec)
        {
            var startTime = DateTime.UtcNow;
            for(;;)
            {
                DateTime endTime = DateTime.UtcNow;
                double delta = (endTime - startTime).TotalMilliseconds;
                if (delta >= minMSec)
                {
                    Console.WriteLine("Sleep asked to wait {0} msec, actually waited {1:n2} msec Start: {2:mm:ss.fff} End: {3:mm:ss.fff} ", minMSec, delta, startTime, endTime);
                    break;
                }
                Thread.Sleep(1);
            }
        }

        private static void ValidateSingleEventCounter(Event evt, string counterName, int count, float mean, float standardDeviation, float min, float max)
        {
            ValidateEventCounter(counterName, count, mean, standardDeviation, min, max, ValidateEventHeaderAndGetPayload(evt));
        }

        private static IDictionary<string, object> ValidateEventHeaderAndGetPayload(Event evt)
        {
            Assert.Equal("EventCounters", evt.EventName);
            Assert.Equal(1, evt.PayloadCount);
            Assert.NotNull(evt.PayloadNames);
            Assert.Equal(1, evt.PayloadNames.Count);
            Assert.Equal("Payload", evt.PayloadNames[0]);
            var ret  = (IDictionary < string, object > ) evt.PayloadValue(0, "Payload");
            Assert.NotNull(ret);
            return ret;
        }

        private static void ValidateEventCounter(string counterName, int count, float mean, float standardDeviation, float min, float max, IDictionary<string, object> payloadContent)
        {
            Assert.Equal(counterName, (string)payloadContent["Name"]);
            Assert.Equal(count, (int)payloadContent["Count"]);
            if (count != 0)
            {
                Assert.Equal(mean, (float)payloadContent["Mean"]);
                Assert.Equal(standardDeviation, (float)payloadContent["StandardDeviation"]);
            }
            Assert.Equal(min, (float)payloadContent["Min"]);
            Assert.Equal(max, (float)payloadContent["Max"]);
        }
    }
}
