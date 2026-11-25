// Simulated engine test bench.
// Publishes CAN frames over UDP and serves setpoints/commands/status over Modbus TCP.
//
//   bench_sim [--can-host 127.0.0.1] [--can-port 20000] [--modbus-port 1502] [--fail-oil-pump-at 30]

#include <arpa/inet.h>
#include <netinet/in.h>
#include <sys/socket.h>
#include <unistd.h>

#include <chrono>
#include <csignal>
#include <cstdlib>
#include <cstring>
#include <iostream>
#include <string>

#include "bench/can.hpp"
#include "bench/engine_model.hpp"
#include "bench/modbus.hpp"

using namespace bench;
using Clock = std::chrono::steady_clock;

namespace {

volatile std::sig_atomic_t g_stop = 0;
void on_signal(int) { g_stop = 1; }

struct Options {
    std::string can_host = "127.0.0.1";
    uint16_t can_port = 20000;
    uint16_t modbus_port = 1502;
    double fail_oil_pump_at = -1;  // seconds of run time, negative = never
};

Options parse(int argc, char** argv) {
    Options o;
    for (int i = 1; i + 1 < argc; i += 2) {
        std::string k = argv[i], v = argv[i + 1];
        if (k == "--can-host") o.can_host = v;
        else if (k == "--can-port") o.can_port = static_cast<uint16_t>(std::stoi(v));
        else if (k == "--modbus-port") o.modbus_port = static_cast<uint16_t>(std::stoi(v));
        else if (k == "--fail-oil-pump-at") o.fail_oil_pump_at = std::stod(v);
        else {
            std::cerr << "Unknown option " << k << "\n";
            std::exit(2);
        }
    }
    return o;
}

CanFrame build(uint32_t id, const std::vector<SignalDef>& sigs, std::initializer_list<double> values) {
    CanFrame f;
    f.id = id;
    auto v = values.begin();
    for (const auto& s : sigs) pack_signal(f, s, *v++);
    return f;
}

}  // namespace

int main(int argc, char** argv) {
    const Options opt = parse(argc, argv);
    std::signal(SIGINT, on_signal);
    std::signal(SIGTERM, on_signal);

    EngineModel engine;
    modbus::RegisterMap regs([&](uint16_t address, uint16_t value) {
        switch (address) {
            case modbus::reg::kSpeedSetpoint:
                if (value > 7000) return false;
                engine.set_speed_setpoint(value);
                return true;
            case modbus::reg::kLoadSetpoint:
                if (value > 400) return false;
                engine.set_load_setpoint(value);
                return true;
            case modbus::reg::kCommand:
                if (value == 0) engine.stop();
                else if (value == 1) engine.start();
                else if (value == 2) engine.reset_fault();
                else return false;
                return true;
            default:
                return false;
        }
    });
    modbus::TcpServer server(regs, opt.modbus_port);

    int udp = ::socket(AF_INET, SOCK_DGRAM, 0);
    sockaddr_in dest{};
    dest.sin_family = AF_INET;
    dest.sin_port = htons(opt.can_port);
    ::inet_pton(AF_INET, opt.can_host.c_str(), &dest.sin_addr);
    auto send = [&](const CanFrame& f) {
        auto bytes = to_udp(f);
        ::sendto(udp, bytes.data(), bytes.size(), 0, reinterpret_cast<sockaddr*>(&dest), sizeof(dest));
    };

    std::cout << "bench_sim: Modbus TCP on port " << opt.modbus_port << ", CAN over UDP to " << opt.can_host << ":"
              << opt.can_port << std::endl;

    constexpr double dt = 0.01;  // 100 Hz model and EngineStatus frame
    auto next = Clock::now();
    long tick = 0;
    while (!g_stop) {
        engine.inject_oil_pump_failure(opt.fail_oil_pump_at >= 0 && engine.state() == BenchState::Running &&
                                       engine.run_time_s() >= opt.fail_oil_pump_at);
        engine.step(dt);
        const auto& o = engine.outputs();

        send(build(ids::kEngineStatus, engine_status_signals(),
                   {o.speed_rpm, o.torque_Nm, o.throttle_pct, static_cast<double>(engine.state())}));
        if (tick % 10 == 0) {
            send(build(ids::kTemperatures, temperature_signals(), {o.coolant_temp_C, o.oil_temp_C, o.exhaust_temp_C}));
            send(build(ids::kPressures, pressure_signals(), {o.oil_pressure_bar, o.boost_pressure_bar}));
        }

        regs.set(modbus::reg::kState, static_cast<uint16_t>(engine.state()));
        regs.set(modbus::reg::kAlarms, engine.alarms());
        regs.set(modbus::reg::kRunTime, static_cast<uint16_t>(engine.run_time_s()));
        regs.set(modbus::reg::kSpeed, static_cast<uint16_t>(o.speed_rpm));

        ++tick;
        next += std::chrono::milliseconds(10);
        // Serve Modbus requests while waiting for the next cycle.
        while (!g_stop) {
            auto left = std::chrono::duration_cast<std::chrono::milliseconds>(next - Clock::now()).count();
            if (left <= 0) break;
            server.poll(static_cast<int>(left));
        }
    }
    ::close(udp);
    std::cout << "bench_sim: stopped" << std::endl;
    return 0;
}
