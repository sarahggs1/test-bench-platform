cmake_minimum_required(VERSION 3.16)
project(bench_sim CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
if(NOT CMAKE_BUILD_TYPE)
  set(CMAKE_BUILD_TYPE Release)
endif()

add_library(bench STATIC src/can.cpp src/engine_model.cpp src/modbus.cpp)
target_include_directories(bench PUBLIC include)
target_compile_options(bench PRIVATE -Wall -Wextra -Wpedantic)

add_executable(bench_sim src/main.cpp)
target_link_libraries(bench_sim PRIVATE bench)

enable_testing()
add_executable(bench_tests tests/test_main.cpp)
target_link_libraries(bench_tests PRIVATE bench)
add_test(NAME bench_tests COMMAND bench_tests)
