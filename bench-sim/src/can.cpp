#include "bench/can.hpp"

#include <algorithm>
#include <cmath>

namespace bench {

namespace {

int64_t raw_min(const SignalDef& s) { return s.is_signed ? -(int64_t{1} << (8 * s.length - 1)) : 0; }
int64_t raw_max(const SignalDef& s) {
    return s.is_signed ? (int64_t{1} << (8 * s.length - 1)) - 1 : (int64_t{1} << (8 * s.length)) - 1;
}

}  // namespace

void pack_signal(CanFrame& frame, const SignalDef& sig, double physical) {
    auto raw = static_cast<int64_t>(std::llround((physical - sig.offset) / sig.factor));
    raw = std::clamp(raw, raw_min(sig), raw_max(sig));
    auto bits = static_cast<uint64_t>(raw);
    for (int i = 0; i < sig.length; ++i) {
        frame.data[sig.start_byte + i] = static_cast<uint8_t>((bits >> (8 * i)) & 0xFF);
    }
}

double unpack_signal(const CanFrame& frame, const SignalDef& sig) {
    uint64_t bits = 0;
    for (int i = 0; i < sig.length; ++i) {
        bits |= static_cast<uint64_t>(frame.data[sig.start_byte + i]) << (8 * i);
    }
    int64_t raw = static_cast<int64_t>(bits);
    const int width = 8 * sig.length;
    if (sig.is_signed && (bits & (uint64_t{1} << (width - 1)))) {
        raw -= int64_t{1} << width;  // two's complement
    }
    return static_cast<double>(raw) * sig.factor + sig.offset;
}

std::vector<uint8_t> to_udp(const CanFrame& frame) {
    std::vector<uint8_t> out(kUdpFrameSize, 0);
    for (int i = 0; i < 4; ++i) out[i] = static_cast<uint8_t>((frame.id >> (8 * i)) & 0xFF);
    out[4] = frame.dlc;
    std::copy(frame.data.begin(), frame.data.end(), out.begin() + 5);
    return out;
}

std::optional<CanFrame> from_udp(const uint8_t* bytes, std::size_t size) {
    if (size != kUdpFrameSize || bytes[4] > 8) return std::nullopt;
    CanFrame f;
    f.id = static_cast<uint32_t>(bytes[0]) | (static_cast<uint32_t>(bytes[1]) << 8) |
           (static_cast<uint32_t>(bytes[2]) << 16) | (static_cast<uint32_t>(bytes[3]) << 24);
    f.dlc = bytes[4];
    std::copy(bytes + 5, bytes + 13, f.data.begin());
    return f;
}

const std::vector<SignalDef>& engine_status_signals() {
    static const std::vector<SignalDef> s{
        {"speed_rpm", 0, 2, false, 1.0, 0.0},
        {"torque_Nm", 2, 2, true, 0.1, 0.0},
        {"throttle_pct", 4, 1, false, 0.5, 0.0},
        {"bench_state", 5, 1, false, 1.0, 0.0},
    };
    return s;
}

const std::vector<SignalDef>& temperature_signals() {
    static const std::vector<SignalDef> s{
        {"coolant_temp_C", 0, 2, true, 0.1, 0.0},
        {"oil_temp_C", 2, 2, true, 0.1, 0.0},
        {"exhaust_temp_C", 4, 2, false, 0.5, 0.0},
    };
    return s;
}

const std::vector<SignalDef>& pressure_signals() {
    static const std::vector<SignalDef> s{
        {"oil_pressure_bar", 0, 2, false, 0.01, 0.0},
        {"boost_pressure_bar", 2, 2, false, 0.01, 0.0},
    };
    return s;
}

}  // namespace bench
