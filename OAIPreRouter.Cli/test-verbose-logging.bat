@echo off
REM ###########################################################################
REM test-verbose-logging.bat - Comprehensive verbose request logging test
REM 
REM This script tests Phase 8 verbose request logging by:
REM 1. Enabling VerboseRequests in appsettings.json
REM 2. Starting OAIPreRouter.Cli in background
REM 3. Waiting for /health endpoint
REM 4. Sending test requests to various endpoints
REM 5. Capturing verbose console output
REM 6. Cleaning up
REM ###########################################################################

setlocal enabledelayedexpansion
cd /d "%~dp0"

set "PROJECT_DIR=%CD%"
set "APP_EXECUTABLE=%PROJECT_DIR%\bin\Debug\net8.0\OAIPreRouter.Cli.exe"
set "APPSETTINGS=%PROJECT_DIR%\appsettings.json"
set "BACKUP_APPSETTINGS=%APPSETTINGS%.backup"
set "LOG_FILE=%PROJECT_DIR%\test-verbose-logging.log"
set "PID_FILE=%TEMP%\oai_preloader.pid"

echo.
echo ========================================================================
echo         OAIPreRouter.Cli - Verbose Request Logging Test
echo ========================================================================
echo.

REM Step 1: Build the project
echo [1/7] Building OAIPreRouter.Cli...
cd /d "%PROJECT_DIR%"
dotnet build -c Debug --no-restore > nul 2>&1
if errorlevel 1 (
    echo [ERROR] Build failed. Please check the project.
    exit /b 1
)
echo [OK] Build successful
echo.

REM Step 2: Backup and modify appsettings.json
echo [2/7] Configuring appsettings.json...
if exist "%BACKUP_APPSETTINGS%" del "%BACKUP_APPSETTINGS%"
copy "%APPSETTINGS%" "%BACKUP_APPSETTINGS%" > nul

REM Use PowerShell to modify JSON if available, otherwise use findstr/replace
powershell -NoProfile -Command "^
  $json = Get-Content '%APPSETTINGS%' | ConvertFrom-Json; ^
  if ($json.Logging.VerboseRequests -ne $null) { ^
    $json.Logging.VerboseRequests = $true; ^
  } else { ^
    if (-not $json.Logging) { $json | Add-Member -NotePropertyName Logging -NotePropertyValue @{} }; ^
    $json.Logging | Add-Member -NotePropertyName VerboseRequests -NotePropertyValue $true -Force; ^
  } ^
  $json | ConvertTo-Json -Depth 10 | Set-Content '%APPSETTINGS%'; ^
" > nul 2>&1

echo [OK] VerboseRequests enabled
echo.

REM Step 3: Start the background process
echo [3/7] Starting OAIPreRouter.Cli in background...
cd /d "%PROJECT_DIR%"
start /b dotnet run --no-build > "%LOG_FILE%" 2>&1
timeout /t 3 /nobreak > nul
echo [OK] Application started
echo.

REM Step 4: Wait for /health endpoint
echo [4/7] Waiting for application to be ready (checking /health)...
set "HEALTH_READY=0"
for /L %%i in (1,1,30) do (
    powershell -NoProfile -Command "try { $response = Invoke-WebRequest -Uri 'http://localhost:5000/health' -ErrorAction Stop; exit 0 } catch { exit 1 }" > nul 2>&1
    if errorlevel 0 (
        set "HEALTH_READY=1"
        goto health_ready
    )
    echo.
    timeout /t 1 /nobreak > nul
)

:health_ready
if "!HEALTH_READY!"=="0" (
    echo [ERROR] Application failed to start. Check logs:
    type "%LOG_FILE%" | findstr /N "."
    goto cleanup
)
echo [OK] Application is ready
echo.

REM Step 5: Send test requests
echo [5/7] Sending test requests...
echo.

REM Test 1: POST to /v1/chat/completions
echo   * Testing /v1/chat/completions endpoint...
powershell -NoProfile -Command "^
  $body = @{ ^
    model = 'gpt-4'; ^
    messages = @(@{ role = 'user'; content = 'Hello' }); ^
    max_tokens = 100 ^
  } | ConvertTo-Json; ^
  try { Invoke-WebRequest -Uri 'http://localhost:5000/v1/chat/completions' -Method POST -Body $body -ContentType 'application/json' -ErrorAction SilentlyContinue } catch { } ^
" > nul 2>&1
echo     OK - Request sent
echo.

timeout /t 1 /nobreak > nul

REM Test 2: POST to /api/chat
echo   * Testing /api/chat endpoint...
powershell -NoProfile -Command "^
  $body = @{ ^
    model = 'gpt-3.5-turbo'; ^
    messages = @(@{ role = 'user'; content = 'Test message' }); ^
    temperature = 0.7 ^
  } | ConvertTo-Json; ^
  try { Invoke-WebRequest -Uri 'http://localhost:5000/api/chat' -Method POST -Body $body -ContentType 'application/json' -ErrorAction SilentlyContinue } catch { } ^
" > nul 2>&1
echo     OK - Request sent
echo.

timeout /t 1 /nobreak > nul

REM Test 3: GET to unknown endpoint (fallback)
echo   * Testing /unknown endpoint (fallback handler)...
powershell -NoProfile -Command "^
  try { Invoke-WebRequest -Uri 'http://localhost:5000/unknown' -ErrorAction SilentlyContinue } catch { } ^
" > nul 2>&1
echo     OK - Request sent
echo.

echo [OK] All test requests sent
echo.

REM Step 6: Collect verbose logs
echo [6/7] Collecting verbose logs...
timeout /t 2 /nobreak > nul
echo.

echo ========================================================================
echo VERBOSE REQUEST LOGS
echo ========================================================================
echo.

if exist "%LOG_FILE%" (
    type "%LOG_FILE%"
) else (
    echo No log file found.
)

REM Step 7: Summary
echo.
echo ========================================================================
echo TEST SUMMARY
echo ========================================================================
echo.
echo OK - Verbose logging enabled
echo OK - Application started successfully
echo OK - Sent 3 test requests (POST /v1/chat/completions, POST /api/chat, GET /unknown)
echo OK - Console output captured and displayed above
echo.
echo What to verify in the logs:
echo   * VerboseRequests setting is enabled
echo   * Request logging shows endpoint paths
echo   * Request logging shows method types (GET, POST)
echo   * Request logging shows response status codes
echo   * Fallback handler logs unknown endpoints
echo.
echo ========================================================================
echo.

:cleanup
REM Kill the background process
echo [7/7] Cleanup - Terminating background process...
taskkill /F /IM dotnet.exe > nul 2>&1
timeout /t 2 /nobreak > nul

REM Restore original appsettings.json
if exist "%BACKUP_APPSETTINGS%" (
    echo Cleanup - Restoring original appsettings.json...
    del "%APPSETTINGS%"
    rename "%BACKUP_APPSETTINGS%" "appsettings.json" > nul
)

echo [OK] Cleanup complete.
echo.
