using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace TouchNStars.PHD2;

public partial class PHD2Client
{
    private readonly object graphLock = new();
    private readonly Queue<JObject> graphSteps = new();
    private bool graphHasAI;
    private long graphId;
    private const int GraphCapacity = 5000;

    private void RecordGraphEvent(JObject evt)
    {
        lock (graphLock)
        {
            var type = (string)evt["Event"];
            if (type == "GuideStep")
            {
                graphHasAI |= evt["AIMode"]?.Type == JTokenType.String;
                var step = (JObject)evt.DeepClone();
                step["Id"] = ++graphId;
                graphSteps.Enqueue(step);
            }
            else if (type == "GuidingDithered")
                graphSteps.Enqueue(new JObject { ["Id"] = ++graphId, ["Dither"] = true });
            while (graphSteps.Count > GraphCapacity) graphSteps.Dequeue();
        }
    }

    public void ClearAIGuideSteps()
    {
        lock (graphLock)
        {
            graphSteps.Clear();
            graphHasAI = false;
        }
    }

    // NINA's graph IDs are independent counters, not PHD2 frame numbers.
    // Keep errors, pulses and AI metadata from the very same native event.
    public JObject GetAIGuideSteps(string expectedHostname, uint expectedInstance, int historySize = 100)
    {
        static string Host(string name) => name?.ToLowerInvariant() is "localhost" or "127.0.0.1" or "::1"
            ? "loopback" : name?.ToLowerInvariant();
        lock (graphLock)
        {
            bool supported = IsConnected && graphHasAI && instance == expectedInstance &&
                Host(hostname) == Host(expectedHostname);
            var steps = new JArray();
            if (supported)
                foreach (var step in graphSteps.Skip(Math.Max(0, graphSteps.Count - Math.Clamp(historySize, 1, GraphCapacity))))
                    steps.Add(step.DeepClone());
            return new JObject { ["Supported"] = supported, ["Steps"] = steps };
        }
    }
}
