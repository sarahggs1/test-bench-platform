// Unit tests of the bench simulator (no external framework: each check reports and counts failures).
#include <cmath>
#include <iostream>
#include <string>

#include "bench/can.hpp"
#include "bench/engine_model.hpp"
#include "bench/modbus.hpp"

using namespace bench;

static int g_failures = 0;
#define CHECK(cond)                                                                    \
    do {                                                                               \
        if (!(cond)) {                                                                 \
            std::cerr << __FILE__ << ":" << __LINE__ << ": CHECK failed: " #cond "\n"; \
            ++g_failures;                                                              \
        }                                                                              \
    } while (0)

static bool near(double a, double b, double tol) { return std::fabs(a - b) <= tol; }

static void test_signal_roundtrip() {
    for (const auto& sigs : {engine_status_signals(), temperature_signals(), pressure_signals()}) {
        for (const auto& s : sigs) {
            CanFrame f;
            const double value = s.is_signed ? -12.3 : 42.0;
            pack_signal(f, s, value);
            CHECK(near(unpack_signal(f, s), value, s.factor));
        }
    }
}

static void test_signal_saturation_and_sign() {
    const SignalDef torque{"torque_Nm", 2, 2, true, 0.1, 0.0};
    CanFrame f;
    pack_signal(f, torque, -250.0);
    CHECK(f.data[2] == 0x3C && f.data[3] == 0xF6);  // -2500 = 0xF63C little-endian
    CHECK(near(unpack_signal(f, torque), -250.0, 1e-9));
    pack_signal(f, torque, 1e9);  // saturates to +32767 raw
    CHECK(near(unpack_signal(f, torque), 3276.7, 1e-9));
}

static void test_udp_format() {
    CanFrame f;
    f.id = 0x200;
    f.data = {1, 2, 3, 4, 5, 6, 7, 8};
    auto bytes = to_udp(f);
    CHECK(bytes.size() == kUdpFrameSize);
    CHECK(bytes[0] == 0x00 && bytes[1] == 0x02 && bytes[4] == 8 && bytes[12] == 8);
    auto back = from_udp(bytes.data(), bytes.size());
    CHECK(back && back->id == 0x200 && back->data == f.data);
    CHECK(!from_udp(bytes.data(), 12));
}

static void test_engine_reaches_setpoint() {
    EngineModel e;
    e.set_speed_setpoint(3000);
    e.set_load_setpoint(150);
    e.start();
    for (int i = 0; i < 1000; ++i) e.step(0.01);  // 10 s
    CHECK(e.state() == BenchState::Running);
    CHECK(near(e.outputs().speed_rpm, 3000, 5));
    CHECK(near(e.outputs().torque_Nm, 150, 1));
    CHECK(e.outputs().oil_pressure_bar > 2.0);
    CHECK(e.alarms() == 0);
}

static void test_oil_pump_failure_trips_bench() {
    EngineModel e;
    e.set_speed_setpoint(2500);
    e.start();
    for (int i = 0; i < 500; ++i) e.step(0.01);
    e.inject_oil_pump_failure(true);
    for (int i = 0; i < 300; ++i) e.step(0.01);
    CHECK(e.state() == BenchState::Fault);
    CHECK(e.alarms() & alarm::kOilPressureLow);
    e.start();  // cannot restart while in fault
    CHECK(e.state() == BenchState::Fault);
    e.reset_fault();
    CHECK(e.state() == BenchState::Stopped && e.alarms() == 0);
}

static std::vector<uint8_t> request(uint8_t function, uint16_t a, uint16_t b) {
    return {0x12, 0x34, 0, 0, 0, 6, 1, function, uint8_t(a >> 8), uint8_t(a), uint8_t(b >> 8), uint8_t(b)};
}

static void test_modbus() {
    uint16_t last_written = 0;
    modbus::RegisterMap map([&](uint16_t, uint16_t v) {
        last_written = v;
        return v <= 7000;
    });
    map.set(modbus::reg::kState, 1);

    auto write = map.handle(request(0x06, modbus::reg::kSpeedSetpoint, 3000));
    CHECK(write == request(0x06, modbus::reg::kSpeedSetpoint, 3000));  // echo
    CHECK(last_written == 3000 && map.get(modbus::reg::kSpeedSetpoint) == 3000);

    auto read = map.handle(request(0x03, 0, 11));
    CHECK(read.size() == 9 + 22);
    CHECK(read[0] == 0x12 && read[1] == 0x34 && read[7] == 0x03 && read[8] == 22);
    CHECK(read[9] == (3000 >> 8) && read[10] == (3000 & 0xFF));
    CHECK(read[9 + 2 * modbus::reg::kState + 1] == 1);

    CHECK(map.handle(request(0x06, modbus::reg::kSpeedSetpoint, 9000))[8] == modbus::kIllegalValue);
    CHECK(map.handle(request(0x06, modbus::reg::kState, 1))[8] == modbus::kIllegalAddress);  // read only
    CHECK(map.handle(request(0x03, 10, 20))[8] == modbus::kIllegalAddress);
    auto bad = map.handle(request(0x10, 0, 1));
    CHECK(bad[7] == 0x90 && bad[8] == modbus::kIllegalFunction);
}

int main() {
    test_signal_roundtrip();
    test_signal_saturation_and_sign();
    test_udp_format();
    test_engine_reaches_setpoint();
    test_oil_pump_failure_trips_bench();
    test_modbus();
    if (g_failures) {
        std::cerr << g_failures << " check(s) failed\n";
        return 1;
    }
    std::cout << "All bench_sim tests passed\n";
    return 0;
}
