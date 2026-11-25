// Minimal Modbus TCP server used by the bench to expose setpoints, commands and status.
// Supported: function 03 (read holding registers) and 06 (write single register).
#pragma once

#include <cstdint>
#include <functional>
#include <vector>

namespace bench::modbus {

// Holding register map of the simulated bench.
namespace reg {
constexpr uint16_t kSpeedSetpoint = 0;  // rpm, read/write
constexpr uint16_t kLoadSetpoint = 1;   // N.m, read/write
constexpr uint16_t kCommand = 2;        // write 0 = stop, 1 = start, 2 = reset fault
constexpr uint16_t kState = 10;         // read only: 0 stopped, 1 running, 2 fault
constexpr uint16_t kAlarms = 11;        // read only: alarm bit field
constexpr uint16_t kRunTime = 12;       // read only: seconds since start
constexpr uint16_t kSpeed = 13;         // read only: measured speed, rpm
constexpr uint16_t kCount = 14;
}  // namespace reg

constexpr uint8_t kIllegalFunction = 0x01;
constexpr uint8_t kIllegalAddress = 0x02;
constexpr uint8_t kIllegalValue = 0x03;

class RegisterMap {
public:
    // Called after a client writes a register; returns false to reject the value.
    using WriteHandler = std::function<bool(uint16_t address, uint16_t value)>;

    explicit RegisterMap(WriteHandler on_write) : on_write_(std::move(on_write)), regs_(reg::kCount, 0) {}

    uint16_t get(uint16_t address) const { return regs_.at(address); }
    void set(uint16_t address, uint16_t value) { regs_.at(address) = value; }

    // Process one complete Modbus TCP request (MBAP header + PDU) and build the response.
    std::vector<uint8_t> handle(const std::vector<uint8_t>& request);

private:
    WriteHandler on_write_;
    std::vector<uint16_t> regs_;
};

// Non-blocking TCP server: call poll() regularly from the main loop.
class TcpServer {
public:
    TcpServer(RegisterMap& map, uint16_t port);
    ~TcpServer();
    TcpServer(const TcpServer&) = delete;
    TcpServer& operator=(const TcpServer&) = delete;

    void poll(int timeout_ms);
    uint16_t port() const { return port_; }

private:
    RegisterMap& map_;
    uint16_t port_;
    int listen_fd_ = -1;
    std::vector<int> clients_;
    std::vector<std::vector<uint8_t>> buffers_;
};

}  // namespace bench::modbus
