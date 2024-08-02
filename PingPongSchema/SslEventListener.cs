using Microsoft.Extensions.Logging;
using System.Diagnostics.Tracing;

namespace Utils {
    public class SslEventListener : EventListener {
        private ILogger _logger;
        public SslEventListener(ILogger logger) {
            _logger = logger;
        }
        protected override void OnEventSourceCreated(EventSource eventSource) {
            if (eventSource.Name.Equals("System.Net.Security") || eventSource.Name.Equals("System.Net.Sockets")) {
                EnableEvents(eventSource, EventLevel.LogAlways, EventKeywords.All);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData) {
            if (eventData.EventName != "EventCounters") {
                _logger.LogInformation($"Event: {eventData.EventName} - {eventData.Message}");
                if (eventData.Payload != null) {
                    foreach (var payload in eventData.Payload) {
                        if (payload is System.Collections.IDictionary payloadDict) {
                            foreach (var key in payloadDict.Keys) {
                                _logger.LogInformation($"{key}: {payloadDict[key]}");
                            }
                        } else {
                            _logger.LogInformation($"Payload: {payload}");
                        }
                    }
                }
            }
        }
    }
}


