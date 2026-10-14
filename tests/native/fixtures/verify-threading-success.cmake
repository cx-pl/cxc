execute_process(COMMAND "${TEST_EXECUTABLE}"
    RESULT_VARIABLE result OUTPUT_VARIABLE output ERROR_VARIABLE diagnostic TIMEOUT 30)
if(NOT result STREQUAL "0")
    message(FATAL_ERROR "Threading fixture failed (${result}): ${output}\n${diagnostic}")
endif()
if(NOT output MATCHES "worker completed")
    message(FATAL_ERROR "Unjoined worker did not finish before process exit: ${output}")
endif()
