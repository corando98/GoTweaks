using System;
using System.Net;

namespace XboxGamingBarHelper.Core
{
    /// <summary>
    /// WebClient with a real timeout. The stock WebClient has NO default timeout
    /// on its underlying HttpWebRequest for DownloadFile — on a machine whose
    /// firewall silently drops outbound traffic (issue #91, Windows LTSC with
    /// strict rules) a download hangs indefinitely with no exception and no UI
    /// feedback. Every helper-side download should use this instead so a blocked
    /// network turns into a WebException the caller's error path can surface.
    /// </summary>
    internal class TimedWebClient : WebClient
    {
        private readonly int _timeoutMs;

        public TimedWebClient(TimeSpan timeout)
        {
            _timeoutMs = (int)timeout.TotalMilliseconds;
        }

        protected override WebRequest GetWebRequest(Uri address)
        {
            var request = base.GetWebRequest(address);
            if (request != null)
            {
                request.Timeout = _timeoutMs;
                if (request is HttpWebRequest http)
                {
                    // ReadWriteTimeout guards the transfer itself (Timeout only
                    // covers connect + first response) so a stalled stream also
                    // errors out instead of hanging forever.
                    http.ReadWriteTimeout = _timeoutMs;
                }
            }
            return request;
        }
    }
}
