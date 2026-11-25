# Interfaces of the simulated bench

## CAN (over UDP)

The bench publishes classic CAN frames. To run anywhere without CAN hardware or the Linux
`vcan` module, each frame is carried in a 13-byte UDP datagram sent to port 20000:

| Bytes | Content |
|---|---|
| 0–3 | CAN identifier, little-endian |
| 4 | DLC (0–8) |
| 5–12 | Data bytes |

Signals are defined once in [`config/can_signals.json`](../config/can_signals.json) (a DBC-like
description shared by the C++ simulator and the C# decoder). All signals are little-endian and
byte-aligned; `physical = raw × factor + offset`.

| ID | Message | Period | Signals |
|---|---|---|---|
| 0x100 | EngineStatus | 10 ms | speed_rpm, torque_Nm (signed, 0.1), throttle_pct (0.5), bench_state |
| 0x200 | Temperatures | 100 ms | coolant_temp_C (0.1), oil_temp_C (0.1), exhaust_temp_C (0.5) |
| 0x300 | Pressures | 100 ms | oil_pressure_bar (0.01), boost_pressure_bar (0.01) |

## Modbus TCP (port 1502, unit 1)

Function codes 03 (read holding registers) and 06 (write single register).

| Register | Access | Meaning |
|---|---|---|
| 0 | R/W | Speed setpoint, rpm (0–7000) |
| 1 | R/W | Load setpoint, N·m (0–400) |
| 2 | W | Command: 0 stop, 1 start, 2 reset fault |
| 10 | R | State: 0 stopped, 1 running, 2 fault |
| 11 | R | Alarms: bit 0 coolant over-temperature (> 105 °C), bit 1 oil pressure low (< 1 bar while running) |
| 12 | R | Run time, s |
| 13 | R | Measured speed, rpm |

Exceptions: 01 illegal function, 02 illegal address (including writes to read-only registers),
03 illegal value (setpoint out of range, unknown command).

An alarm while running trips the bench to the fault state (safe stop); it must be reset before a
new start.

## Portal API (HTTP, port 5080)

| Method and path | Purpose |
|---|---|
| `GET /api/bench/status` | Modbus status, CAN reception, active run |
| `POST /api/bench/reset` | Reset a bench fault |
| `GET /api/live` | Latest value of every signal |
| `GET /api/signals`, `GET /api/limits` | Signal database and acceptance limits |
| `GET/POST /api/configurations`, `POST /api/configurations/{id}/status` | Configuration lifecycle: draft → validated → deployed |
| `GET/POST /api/tests`, `POST /api/tests/{id}/start` | Test planning and start (only on the deployed configuration) |
| `GET /api/runs`, `GET /api/runs/{id}`, `POST /api/runs/{id}/stop` | Runs, verdicts, operator stop |
| `GET /api/runs/{id}/measurements?signal=` | Recorded signal, decimated for display |
| `GET/POST /api/release-notes` | Release notes |
