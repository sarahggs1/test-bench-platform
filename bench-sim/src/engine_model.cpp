#include "bench/engine_model.hpp"

#include <algorithm>
#include <cmath>

namespace bench {

namespace {
constexpr double kMaxSpeed = 7000.0;
constexpr double kMaxTorque = 400.0;
constexpr double kIdleSpeed = 800.0;

// First-order lag: y moves towards target with time constant tau.
double lag(double y, double target, double tau, double dt) { return y + (target - y) * (1.0 - std::exp(-dt / tau)); }
}  // namespace

EngineModel::EngineModel(double ambient_C) : ambient_C_(ambient_C) {
    out_.coolant_temp_C = out_.oil_temp_C = out_.exhaust_temp_C = ambient_C;
}

void EngineModel::set_speed_setpoint(double rpm) { speed_sp_ = std::clamp(rpm, 0.0, kMaxSpeed); }
void EngineModel::set_load_setpoint(double torque_Nm) { load_sp_ = std::clamp(torque_Nm, 0.0, kMaxTorque); }

void EngineModel::start() {
    if (state_ == BenchState::Stopped) {
        state_ = BenchState::Running;
        run_time_s_ = 0;
    }
}

void EngineModel::stop() {
    if (state_ == BenchState::Running) state_ = BenchState::Stopped;
}

void EngineModel::reset_fault() {
    if (state_ == BenchState::Fault) {
        state_ = BenchState::Stopped;
        alarms_ = 0;
    }
}

void EngineModel::step(double dt) {
    const bool running = state_ == BenchState::Running;
    if (running) run_time_s_ += dt;

    const double speed_target = running ? std::max(speed_sp_, kIdleSpeed) : 0.0;
    const double torque_target = running ? load_sp_ : 0.0;
    out_.speed_rpm = lag(out_.speed_rpm, speed_target, 0.8, dt);
    out_.torque_Nm = lag(out_.torque_Nm, torque_target, 0.4, dt);

    // Power demand drives throttle, boost and heat.
    const double load_ratio = std::clamp(out_.torque_Nm / kMaxTorque, 0.0, 1.0);
    const double speed_ratio = std::clamp(out_.speed_rpm / kMaxSpeed, 0.0, 1.0);
    const double power_ratio = load_ratio * speed_ratio;

    out_.throttle_pct = running ? std::clamp(8.0 + 92.0 * std::sqrt(power_ratio), 0.0, 100.0) : 0.0;
    out_.boost_pressure_bar = lag(out_.boost_pressure_bar, 1.0 + 1.2 * power_ratio, 0.5, dt);

    // Thermostat opens around 90 degC: the coolant settles near 90 + a load-dependent offset.
    const double coolant_target = running ? 90.0 + 12.0 * power_ratio : ambient_C_;
    out_.coolant_temp_C = lag(out_.coolant_temp_C, coolant_target, 60.0, dt);
    out_.oil_temp_C = lag(out_.oil_temp_C, running ? coolant_target + 8.0 : ambient_C_, 90.0, dt);
    out_.exhaust_temp_C = lag(out_.exhaust_temp_C, running ? 350.0 + 550.0 * power_ratio : ambient_C_, 5.0, dt);

    const double oil_target = (running && !oil_pump_failed_) ? 1.5 + 3.5 * speed_ratio : 0.0;
    out_.oil_pressure_bar = lag(out_.oil_pressure_bar, oil_target, 0.3, dt);

    // Alarms: an alarm while running trips the bench to Fault (safe stop).
    uint16_t active = 0;
    if (out_.coolant_temp_C > 105.0) active |= alarm::kCoolantOverTemp;
    if (running && run_time_s_ > 3.0 && out_.oil_pressure_bar < 1.0) active |= alarm::kOilPressureLow;
    alarms_ |= active;
    if (running && active) state_ = BenchState::Fault;
}

}  // namespace bench
