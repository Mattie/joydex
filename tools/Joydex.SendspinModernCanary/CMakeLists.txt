cmake_minimum_required(VERSION 3.20)

project(JoydexSendspinModernCanary LANGUAGES CXX)

if(NOT DEFINED SENDSPIN_CPP_SOURCE_DIR)
  message(FATAL_ERROR "Set SENDSPIN_CPP_SOURCE_DIR to the pinned sendspin-cpp checkout")
endif()

set(SENDSPIN_CPP_EXPECTED_COMMIT "30514d5102c269a0c7fa6a13932d6bf7f2ae1abc")
find_package(Git REQUIRED)
execute_process(
  COMMAND ${GIT_EXECUTABLE} -c safe.directory=${SENDSPIN_CPP_SOURCE_DIR}
          -C ${SENDSPIN_CPP_SOURCE_DIR} rev-parse HEAD
  OUTPUT_VARIABLE SENDSPIN_CPP_ACTUAL_COMMIT
  OUTPUT_STRIP_TRAILING_WHITESPACE
  RESULT_VARIABLE SENDSPIN_CPP_REV_PARSE_RESULT
)
if(NOT SENDSPIN_CPP_REV_PARSE_RESULT EQUAL 0 OR
   NOT SENDSPIN_CPP_ACTUAL_COMMIT STREQUAL SENDSPIN_CPP_EXPECTED_COMMIT)
  message(FATAL_ERROR
    "sendspin-cpp must be pinned at ${SENDSPIN_CPP_EXPECTED_COMMIT}; got ${SENDSPIN_CPP_ACTUAL_COMMIT}")
endif()
execute_process(
  COMMAND ${GIT_EXECUTABLE} -c safe.directory=${SENDSPIN_CPP_SOURCE_DIR}
          -C ${SENDSPIN_CPP_SOURCE_DIR} diff HEAD --ignore-space-at-eol --quiet --exit-code
  RESULT_VARIABLE SENDSPIN_CPP_DIFF_RESULT
)
if(NOT SENDSPIN_CPP_DIFF_RESULT EQUAL 0)
  message(FATAL_ERROR "sendspin-cpp checkout has tracked source changes")
endif()

set(BUILD_EXAMPLES OFF CACHE BOOL "" FORCE)
set(SENDSPIN_BUILD_TESTS OFF CACHE BOOL "" FORCE)
set(SENDSPIN_ENABLE_PLAYER ON CACHE BOOL "" FORCE)
set(SENDSPIN_ENABLE_CONTROLLER OFF CACHE BOOL "" FORCE)
set(SENDSPIN_ENABLE_METADATA OFF CACHE BOOL "" FORCE)
set(SENDSPIN_ENABLE_COLOR OFF CACHE BOOL "" FORCE)
set(SENDSPIN_ENABLE_ARTWORK OFF CACHE BOOL "" FORCE)
set(SENDSPIN_ENABLE_VISUALIZER OFF CACHE BOOL "" FORCE)

add_subdirectory(${SENDSPIN_CPP_SOURCE_DIR} sendspin-cpp)

add_executable(joydex-sendspin-modern-canary main.cpp)
target_compile_features(joydex-sendspin-modern-canary PRIVATE cxx_std_20)
target_link_libraries(joydex-sendspin-modern-canary PRIVATE sendspin)
