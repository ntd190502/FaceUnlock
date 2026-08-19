cmake_minimum_required(VERSION 3.20)
project(FaceUnlockAuthPackageHarness LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

add_definitions(
    -DUNICODE
    -D_UNICODE
    -DWIN32_LEAN_AND_MEAN
    -DSECURITY_WIN32
)

add_executable(FaceUnlockAuthPackageHarness
    AuthPackageHarness.cpp
    ../FaceUnlock.AuthPackage/AuthPackageCore.cpp
)

target_link_libraries(FaceUnlockAuthPackageHarness PRIVATE
    bcrypt
    crypt32
    advapi32
    secur32
)
