// SPDX-License-Identifier: MIT
namespace SDRSharp.AircraftDataEnhanced;

internal static class VerifiedAircraftMessagePolicy
{
    public static bool TryAccept(
        Vdl2Message message,
        out Vdl2Message verifiedMessage,
        out string rejectionReason)
    {
        ArgumentNullException.ThrowIfNull(
            message);

        verifiedMessage =
            message;

        rejectionReason =
            string.Empty;

        if (!message.Valid)
        {
            rejectionReason =
                "message_not_valid";

            return false;
        }

        if (!IsVerifiedProtocol(
            message.Protocol))
        {
            rejectionReason =
                "protocol_not_verified_avlc";

            return false;
        }

        if (!AircraftOnlineLookup.TryNormalizeIcao(
            message.Icao,
            out var normalizedIcao))
        {
            rejectionReason =
                "icao24_missing_or_invalid";

            return false;
        }

        verifiedMessage =
            message with
            {
                Icao =
                    normalizedIcao
            };

        return true;
    }

    public static bool IsVerifiedProtocol(
        string? protocol)
    {
        return
            string.Equals(
                protocol,
                "ACARS",
                StringComparison.OrdinalIgnoreCase)
            ||
            string.Equals(
                protocol,
                "AVLC",
                StringComparison.OrdinalIgnoreCase);
    }
}
