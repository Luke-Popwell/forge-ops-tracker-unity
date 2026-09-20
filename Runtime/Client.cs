using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace ForgeOpsTracker.Unity
{
    /// <summary>
    /// Delivers one payload over HTTP via <see cref="UnityWebRequest"/>, not
    /// <c>System.Net.Http.HttpClient</c> or a raw socket: <c>UnityWebRequest</c> is the only
    /// HTTP client Unity guarantees works identically across every platform this SDK targets,
    /// including WebGL, where builds run inside a browser sandbox with no raw socket access at
    /// all and networking has to go through the browser's own <c>XMLHttpRequest</c>
    /// (<c>UnityWebRequest</c> does this transparently on that platform; <c>HttpClient</c> does
    /// not work there). This is the same reasoning every real Unity error-reporting SDK's own
    /// public documentation gives for the same choice.
    ///
    /// Must run as a coroutine driven by an active MonoBehaviour (see
    /// <see cref="ForgeOpsTrackerDriver"/>): <c>UnityWebRequest</c>'s async operation is
    /// polled by Unity's own player loop, so sending one is only meaningful from Unity's main
    /// thread, and only while something is actually yielding on it each frame.
    /// </summary>
    internal static class Client
    {
        public static IEnumerator DeliverCoroutine(Configuration configuration, Dictionary<string, object> payload, Action<bool> onComplete, DeliveryTarget target = DeliveryTarget.Events)
        {
            string uri;
            switch (target)
            {
                case DeliveryTarget.PerformanceSamples: uri = configuration.PerformanceSamplesUri(); break;
                case DeliveryTarget.Spans: uri = configuration.SpansUri(); break;
                case DeliveryTarget.CustomMetrics: uri = configuration.CustomMetricsUri(); break;
                case DeliveryTarget.InfrastructureMetrics: uri = configuration.InfrastructureMetricsUri(); break;
                default: uri = configuration.IngestionUri(); break;
            }
            var apiKey = configuration.ApiKey;

            if (uri == null || apiKey == null)
            {
                onComplete?.Invoke(false);
                yield break;
            }

            var body = Encoding.UTF8.GetBytes(Json.Encode(payload));

            using (var request = new UnityWebRequest(uri, UnityWebRequest.kHttpVerbPOST))
            {
                request.uploadHandler = new UploadHandlerRaw(body);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Authorization", $"Bearer {apiKey}");
                request.timeout = Mathf.Max(1, configuration.TimeoutMillis / 1000);

                yield return request.SendWebRequest();

#if UNITY_2020_1_OR_NEWER
                var failed = request.result != UnityWebRequest.Result.Success;
#else
                var failed = request.isNetworkError || request.isHttpError;
#endif

                if (failed)
                {
                    configuration.Log($"[ForgeOpsTracker] delivery failed: {request.error}");
                }

                onComplete?.Invoke(!failed);
            }
        }
    }
}
