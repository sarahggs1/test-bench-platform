// CAN frames, signal packing and the CAN-over-UDP transport format.
#pragma once

#include <array>
#include <cstdint>
#include <optional>
#include <string>
#include <vector>

namespace bench {

struct CanFrame {
    uint32_t id = 0;
    uint8_t dlc = 8;
    std::array<uint8_t, 8> data{};
};

// Byte-aligned, little-endian signal definition (same layout as config/can_signals.json).
struct SignalDef {
    std::string name;
    int start_byte;
    int length;  // 1 or 2 bytes
    bool is_signed;
    double factor;
    double offset;
};

// Physical value -> raw bits written into the frame (saturated to the signal range).
void pack_signal(CanFrame& frame, const SignalDef& sig, double physical);

// Raw bits read from the frame -> physical value.
double unpack_signal(const CanFrame& frame, const SignalDef& sig);

// CAN-over-UDP datagram: 4 bytes identifier (little-endian), 1 byte DLC, 8 data bytes.
constexpr std::size_t kUdpFrameSize = 13;
std::vector<uint8_t> to_udp(const CanFrame& frame);
std::optional<CanFrame> from_udp(const uint8_t* bytes, std::size_t size);

// Signal database of the simulated bench.
namespace ids {
constexpr uint32_t kEngineStatus = 0x100;
constexpr uint32_t kTemperatures = 0x200;
constexpr uint32_t kPressures = 0x300;
}  // namespace ids

const std::vector<SignalDef>& engine_status_signals();
const std::vector<SignalDef>& temperature_signals();
const std::vector<SignalDef>& pressure_signals();

}  // namespace bench
