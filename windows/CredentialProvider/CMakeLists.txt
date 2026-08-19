cmake_minimum_required(VERSION 3.24)
project(FaceUnlockCredentialProvider LANGUAGES CXX)

add_library(FaceUnlockCredentialProvider SHARED
    dllmain.cpp
    FaceUnlockCredentialProvider.cpp
    FaceUnlockIpcClient.cpp
    FaceUnlockCredentialProvider.def
)

target_compile_features(FaceUnlockCredentialProvider PRIVATE cxx_std_20)
target_compile_definitions(FaceUnlockCredentialProvider PRIVATE UNICODE _UNICODE)
target_link_libraries(FaceUnlockCredentialProvider PRIVATE
    Ole32
    Credui
    Advapi32
    Shlwapi
    Secur32
    Crypt32
)

set_target_properties(FaceUnlockCredentialProvider PROPERTIES
    PREFIX ""
    OUTPUT_NAME "FaceUnlockCredentialProvider"
)
