// Simplified engine on a dynamometer: speed and load setpoints, first-order dynamics,
// thermal behaviour and alarms. Good enough to exercise acquisition and supervision
// software; not a physical engine model.
#pragma once

#include <cstdint>

namespace bench {

enum class BenchState : uint8_t { Stopped = 0, Running = 1, Fault = 2 };

namespace alarm {
constexpr uint16_t kCoolantOverTemp = 1 << 0;  // coolant above 105 degC
constexpr uint16_t kOilPressureLow = 1 << 1;   // oil pressure below 1.0 bar while running
}  // namespace alarm

struct EngineOutputs {
    double speed_rpm = 0;
    double torque_Nm = 0;
    double throttle_pct = 0;
    double coolant_temp_C = 20;
    double oil_temp_C = 20;
    double exhaust_temp_C = 20;
    double oil_pressure_bar = 0;
    double boost_pressure_bar = 0;
};

class EngineModel {
public:
    explicit EngineModel(double ambient_C = 20.0);

    void set_speed_setpoint(double rpm);
    void set_load_setpoint(double torque_Nm);
    void start();
    void stop();
    void reset_fault();

    // Advance the simulation by dt seconds.
    void step(double dt);

    const EngineOutputs& outputs() const { return out_; }
    BenchState state() const { return state_; }
    uint16_t alarms() const { return alarms_; }
    double run_time_s() const { return run_time_s_; }
    double speed_setpoint() const { return speed_sp_; }
    double load_setpoint() const { return load_sp_; }

    // Test hook: simulate an oil pump failure.
    void inject_oil_pump_failure(bool failed) { oil_pump_failed_ = failed; }

private:
    double ambient_C_;
    double speed_sp_ = 0;
    double load_sp_ = 0;
    BenchState state_ = BenchState::Stopped;
    uint16_t alarms_ = 0;
    double run_time_s_ = 0;
    bool oil_pump_failed_ = false;
    EngineOutputs out_;
};

}  // namespace bench
