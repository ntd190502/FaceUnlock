cmake_minimum_required(VERSION 3.20)
project(FaceUnlockAuthPackage LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

add_definitions(
    -DUNICODE
    -D_UNICODE
    -DWIN32_LEAN_AND_MEAN
    -DSECURITY_WIN32
)

add_library(FaceUnlockAuthPackage SHARED
    FaceUnlockAuthCommon.h
    AuthPackageCore.h
    AuthPackageCore.cpp
    LsaAuthPackage.cpp
    FaceUnlockAuthPackage.def
)

target_link_libraries(FaceUnlockAuthPackage PRIVATE
    bcrypt
    crypt32
    advapi32
    secur32
)

set_target_properties(FaceUnlockAuthPackage PROPERTIES
    LINK_FLAGS "/DEF:\"${CMAKE_CURRENT_SOURCE_DIR}/FaceUnlockAuthPackage.def\""
)
