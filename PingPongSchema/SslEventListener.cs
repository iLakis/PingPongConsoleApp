using System.Diagnostics.Tracing;

public class SslEventListener : EventListener {
    protected override void OnEventSourceCreated(EventSource eventSource) {
        if (eventSource.Name.Equals("System.Net.Security") || eventSource.Name.Equals("System.Net.Sockets")) {
            EnableEvents(eventSource, EventLevel.LogAlways, EventKeywords.All);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData) {
        if (eventData.EventName != "EventCounters") {
            Console.WriteLine($"Event: {eventData.EventName} - {eventData.Message}");
            if (eventData.Payload != null) {
                foreach (var payload in eventData.Payload) {
                    if (payload is System.Collections.IDictionary payloadDict) {
                        foreach (var key in payloadDict.Keys) {
                            Console.WriteLine($"{key}: {payloadDict[key]}");
                        }
                    } else {
                        Console.WriteLine($"Payload: {payload}");
                    }
                }
            }
        }
    }
}
