execute_process(COMMAND "${TEST_EXECUTABLE}" "${TEST_MODE}"
    RESULT_VARIABLE result OUTPUT_VARIABLE output ERROR_VARIABLE diagnostic TIMEOUT 30)
if(NOT result STREQUAL "1")
    message(FATAL_ERROR "Expected exit 1 for ${TEST_MODE}, got ${result}: ${output}\n${diagnostic}")
endif()
if(NOT diagnostic MATCHES "${EXPECTED_DIAGNOSTIC}")
    message(FATAL_ERROR "Missing '${EXPECTED_DIAGNOSTIC}': ${diagnostic}")
endif()
