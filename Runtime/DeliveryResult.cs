namespace ForgeOpsTracker.Unity
{
    /// <summary>
    /// How one delivery ended, as far as whoever holds the payload is concerned: gone out, never
    /// going to go out, or worth another try. <see cref="DeliveryQueue"/>'s public callbacks still
    /// carry a plain bool; this is what the driver hands the flushers that keep a batch between
    /// attempts.
    /// </summary>
    internal enum DeliveryResult
    {
        /// <summary>Any 2xx.</summary>
        Delivered,

        /// <summary>
        /// A 4xx other than 408 and 429: the server will never take this payload (a bad key, a plan
        /// without the feature, a body it can't parse or that is too large), so sending it again
        /// only repeats the answer.
        /// </summary>
        Rejected,

        /// <summary>A network error, a timeout, a 5xx, a 408 or a 429: the same payload may well go through later.</summary>
        Failed
    }

    internal static class DeliveryResults
    {
        /// <summary>
        /// How many flushes in a row may fail before a batch is dropped anyway, so an endpoint that
        /// stays down can't make every later flush carry (and a full buffer keep refusing) more and
        /// more.
        /// </summary>
        public const int MaxFailedFlushes = 5;

        /// <param name="succeeded">Whether <c>UnityWebRequest</c> itself reported success.</param>
        /// <param name="responseCode"><c>UnityWebRequest.responseCode</c>: the HTTP status, or 0 when no response arrived at all.</param>
        public static DeliveryResult FromResponse(bool succeeded, long responseCode)
        {
            if (succeeded) return DeliveryResult.Delivered;
            if (responseCode >= 400 && responseCode < 500 && responseCode != 408 && responseCode != 429) return DeliveryResult.Rejected;
            return DeliveryResult.Failed;
        }

        public static DeliveryResult FromBool(bool delivered) => delivered ? DeliveryResult.Delivered : DeliveryResult.Failed;

        /// <summary>Why a batch that ended this way is let go, for the debug log.</summary>
        public static string DropReason(DeliveryResult result) =>
            result == DeliveryResult.Rejected ? "the server rejected it" : $"delivery failed {MaxFailedFlushes} times in a row";
    }
}
