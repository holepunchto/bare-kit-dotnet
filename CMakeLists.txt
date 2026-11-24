cmake_minimum_required(VERSION 4.0)

find_package(cmake-bare REQUIRED PATHS node_modules/cmake-bare)
find_package(cmake-fetch REQUIRED PATHS node_modules/cmake-fetch)

project(bare_kit_dotnet C)

fetch_package("github:holepunchto/bare-kit@3.2.1")

add_custom_target(bare_kit_dotnet ALL DEPENDS bare_kit::shared)

bare_target(target)

string(REPLACE "darwin-" "osx-" rid ${target})
string(REPLACE "win32-" "win-" rid ${rid})

install(
  TARGETS bare_kit_shared
  LIBRARY DESTINATION runtimes/${rid}/native
  RUNTIME DESTINATION runtimes/${rid}/native
)
