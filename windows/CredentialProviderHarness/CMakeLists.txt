cmake_minimum_required(VERSION 3.24)
project(CredentialProviderHarness LANGUAGES CXX)

add_executable(CredentialProviderHarness
    CredentialProviderHarness.cpp
)

target_compile_features(CredentialProviderHarness PRIVATE cxx_std_20)
target_compile_definitions(CredentialProviderHarness PRIVATE UNICODE _UNICODE)
target_link_libraries(CredentialProviderHarness PRIVATE
    Ole32
    Credui
    Advapi32
)

# Set subsystem to console for wmain
set_target_properties(CredentialProviderHarness PROPERTIES
    WIN32_EXECUTABLE FALSE
    OUTPUT_NAME "CredentialProviderHarness"
)
