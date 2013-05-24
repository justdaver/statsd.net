﻿using statsd.net.Messages;
using statsd.net.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace statsd.net.Framework
{
  public class TimedLatencyPercentileAggregatorBlockFactory
  {
    public static ActionBlock<StatsdMessage> CreateBlock(ITargetBlock<GraphiteLine> target,
      string rootNamespace, 
      IIntervalService intervalService,
      int percentile)
    {
      var latencies = new Dictionary<string, List<int>>();
      var root = rootNamespace;
      var spinLock = new SpinLock();
      var ns = String.IsNullOrEmpty(rootNamespace) ? "" : rootNamespace + ".";
      var blockOptions = new ExecutionDataflowBlockOptions() { MaxDegreeOfParallelism = 1 };
      var busyProcessingTimerHandle = new ManualResetEvent(false);

      var incoming = new ActionBlock<StatsdMessage>(p =>
        {
          bool gotLock = false;
          var latency = p as Timing;
          try
          {
            spinLock.Enter(ref gotLock);
            if (latencies.ContainsKey(latency.Name))
            {
              latencies[latency.Name].Add(latency.ValueMS);
            }
            else
            {
              latencies.Add(latency.Name, new List<int>() { latency.ValueMS });
            }
          }
          finally
          {
            if (gotLock)
            {
              spinLock.Exit(false);
            }
          }
        },
        new ExecutionDataflowBlockOptions() { MaxDegreeOfParallelism = 1 });
      intervalService.Elapsed += (sender, e) =>
        {
          if (latencies.Count == 0)
          {
            return;
          }
          bool gotLock = false;
          Dictionary<string, List<int>> bucketOfLatencies = null;
          try
          {
            spinLock.Enter(ref gotLock);
            bucketOfLatencies = latencies;
            latencies = new Dictionary<string, List<int>>();
          }
          finally
          {
            if (gotLock)
            {
              spinLock.Exit(false);
            }
          }
          int percentileValue;
          foreach (var measurements in bucketOfLatencies)
          {
            if (Percentile.TryCompute(measurements.Value, percentile, out percentileValue))
            {
              target.Post(new GraphiteLine(ns + measurements.Key + ".p" + percentile, percentileValue, e.Epoch));
            }
          }
        };
      incoming.Completion.ContinueWith(p =>
        {
          // Stop the timer
          intervalService.Cancel();
          // Tell the upstream block that we're done
          target.Complete();
        });
      intervalService.Start();
      return incoming;
    }
  }
}
