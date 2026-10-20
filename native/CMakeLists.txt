# daml_ts.dll: the tree-sitter runtime and the Daml grammar in one library, for hosts that load the
# parser themselves rather than through a tree-sitter binding. It exports the runtime's C API
# (what tree_sitter/api.h declares, bar the Wasm functions) and tree_sitter_daml, which
# include/tree_sitter/tree-sitter-daml.h declares.
#
#   cmake -S native -B build/native/x64 -A x64        or -A ARM64
#   cmake --build build/native/x64 --config Release
#   ctest --test-dir build/native/x64 -C Release
#
# native/build.ps1 does all of it with the CMake and compilers of Visual Studio. The parser is
# generated: run `npx tree-sitter generate` in grammar/tree-sitter-daml first.
cmake_minimum_required(VERSION 3.21)

# The library carries the grammar's version, the one tree-sitter.json gives.
set(grammar "${CMAKE_CURRENT_SOURCE_DIR}/../grammar/tree-sitter-daml")
set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS "${grammar}/tree-sitter.json")
file(READ "${grammar}/tree-sitter.json" metadata)
string(JSON version GET "${metadata}" metadata version)

project(daml_ts VERSION ${version} LANGUAGES C)
set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)

if(NOT MSVC)
  message(FATAL_ERROR "daml_ts is built with MSVC for Windows; elsewhere use `tree-sitter build`.")
endif()

if(NOT EXISTS "${grammar}/src/parser.c")
  message(FATAL_ERROR "No generated parser: run `npx tree-sitter generate` in grammar/tree-sitter-daml.")
endif()

# The runtime at the pinned commit, fetched into the build tree: lib/src, lib/include and the
# license that goes out with the library.
set(pin "${CMAKE_CURRENT_SOURCE_DIR}/../grammar/tree-sitter-runtime.txt")
set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS "${pin}")
file(STRINGS "${pin}" commit REGEX "^commit=")
string(REGEX REPLACE "^commit=([0-9a-f]+).*" "\\1" commit "${commit}")
set(runtime "${CMAKE_BINARY_DIR}/tree-sitter")

# The patches in grammar/runtime-patches/ go on top of the checkout. A marker records which ones
# were applied, and the checkout is made afresh when the commit or the set of patches changes.
file(GLOB patches CONFIGURE_DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/../grammar/runtime-patches/*.patch")
list(SORT patches)
set(wanted_patches "")
foreach(patch IN LISTS patches)
  set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS "${patch}")
  file(SHA256 "${patch}" digest)
  get_filename_component(name "${patch}" NAME)
  string(APPEND wanted_patches "${name} ${digest}\n")
endforeach()
set(applied_marker "${runtime}/applied-patches.txt")
set(applied_patches "")
if(EXISTS "${applied_marker}")
  file(READ "${applied_marker}" applied_patches)
endif()

execute_process(COMMAND git -C "${runtime}" rev-parse HEAD
  OUTPUT_VARIABLE head OUTPUT_STRIP_TRAILING_WHITESPACE ERROR_QUIET)
if(NOT head STREQUAL commit OR NOT EXISTS "${runtime}/LICENSE"
    OR NOT applied_patches STREQUAL wanted_patches)
  message(STATUS "Fetching the tree-sitter runtime at ${commit}")
  file(REMOVE_RECURSE "${runtime}")
  file(MAKE_DIRECTORY "${runtime}")
  foreach(step
      "init -q"
      "remote add origin https://github.com/tree-sitter/tree-sitter"
      "sparse-checkout set --no-cone /lib/src/ /lib/include/ /LICENSE"
      "fetch -q --depth 1 origin ${commit}"
      "checkout -q FETCH_HEAD")
    separate_arguments(arguments NATIVE_COMMAND "${step}")
    execute_process(COMMAND git -C "${runtime}" ${arguments} RESULT_VARIABLE failed)
    if(failed)
      message(FATAL_ERROR "Fetching the tree-sitter runtime failed at `git ${step}`.")
    endif()
  endforeach()
  foreach(patch IN LISTS patches)
    execute_process(COMMAND git -C "${runtime}" apply --whitespace=nowarn "${patch}" RESULT_VARIABLE failed)
    if(failed)
      message(FATAL_ERROR "The runtime patch ${patch} doesn't apply to ${commit}.")
    endif()
  endforeach()
  file(WRITE "${applied_marker}" "${wanted_patches}")
endif()

# The exports: every function api.h declares (its declarations start in the first column) and the
# language. A name the runtime doesn't define fails the link, so the list can't drift from the pin.
# The runtime is built without its Wasm feature (that needs wasmtime), so the Wasm functions stay out.
file(STRINGS "${runtime}/lib/include/tree_sitter/api.h" declarations
  REGEX "^[A-Za-z].*[^A-Za-z0-9_]ts_[a-z0-9_]+\\(")
set(exports tree_sitter_daml)
foreach(declaration IN LISTS declarations)
  string(REGEX MATCH "ts_[a-z0-9_]+\\(" name "${declaration}")
  if(name AND NOT name MATCHES "wasm")
    string(REPLACE "(" "" name "${name}")
    list(APPEND exports "${name}")
  endif()
endforeach()
list(REMOVE_DUPLICATES exports)
list(LENGTH exports export_count)
message(STATUS "daml_ts exports ${export_count} functions")
list(JOIN exports "\n  " export_lines)
file(CONFIGURE OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/daml_ts.def"
  CONTENT "LIBRARY daml_ts\nEXPORTS\n  ${export_lines}\n")

add_library(daml_ts SHARED
  "${runtime}/lib/src/lib.c"
  "${grammar}/src/parser.c"
  "${grammar}/src/scanner.c"
  "${CMAKE_CURRENT_BINARY_DIR}/daml_ts.def")
target_include_directories(daml_ts
  PUBLIC "${runtime}/lib/include" "${CMAKE_CURRENT_SOURCE_DIR}/include"
  PRIVATE "${runtime}/lib/src" "${grammar}/src")
# The .def file is the one export list; without this the parser would export its language itself.
target_compile_definitions(daml_ts PRIVATE TREE_SITTER_HIDE_SYMBOLS _CRT_SECURE_NO_WARNINGS)
# Optimised, with control-flow guard and symbols for crash dumps. The C runtime is the shared one
# (/MD, CMake's default): strings and arrays the API hands back for the caller to free() come from
# the same heap as the host's.
target_compile_options(daml_ts PRIVATE /O2 /guard:cf /Zi /utf-8)
target_link_options(daml_ts PRIVATE /guard:cf /DEBUG /OPT:REF /OPT:ICF)

add_executable(smoke test/smoke.c)
target_link_libraries(smoke PRIVATE daml_ts)
target_compile_definitions(smoke PRIVATE
  DAML_TS_VERSION_MAJOR=${PROJECT_VERSION_MAJOR}
  DAML_TS_VERSION_MINOR=${PROJECT_VERSION_MINOR}
  DAML_TS_VERSION_PATCH=${PROJECT_VERSION_PATCH})
target_compile_options(smoke PRIVATE /W4 /WX /utf-8)

# The test runs only where the machine can run what was built: ARM64 is cross-compiled on x64.
enable_testing()
string(REPLACE "AMD64" "x64" host "${CMAKE_HOST_SYSTEM_PROCESSOR}")
if(host STREQUAL CMAKE_C_COMPILER_ARCHITECTURE_ID)
  add_test(NAME smoke COMMAND smoke)
endif()
