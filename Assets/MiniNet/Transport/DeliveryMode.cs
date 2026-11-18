using System;
using MiniNet.Kcp2k;

namespace MiniNet {
    public enum DeliveryMode {
        Reliable,
        Unreliable,
    }

    internal static class KcpDeliveryMode {
        public static KcpChannel ToKcp(DeliveryMode mode) => mode switch {
            DeliveryMode.Reliable => KcpChannel.Reliable,
            DeliveryMode.Unreliable => KcpChannel.Unreliable,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
        };

        public static DeliveryMode FromKcp(KcpChannel channel) => channel switch {
            KcpChannel.Reliable => DeliveryMode.Reliable,
            KcpChannel.Unreliable => DeliveryMode.Unreliable,
            _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, null),
        };
    }
}
