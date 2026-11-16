@echo off
setlocal EnableExtensions

REM ===========================================================================
REM  RunPipeline.bat  -  batch DWG data harvest
REM
REM  Loads AEC_Batch_Data_Haravester.dll into one AutoCAD Core Console process and
REM  runs the HarvestBatch command, which opens each drawing in Input_DWGs as a side
REM  database. Startup is paid once per chunk, not once per drawing.
REM
REM  Each drawing writes its own file, Output\_rows\<key>.csv, temp-then-rename.
REM  The merged Output\Extracted_BOM.csv is built from those files at the end.
REM  The supervisor owns the key (tools\build-manifest.ps1) and the plugin uses
REM  it verbatim, so both halves agree on which files belong to which drawing.
REM
REM  A drawing that throws is a .fail and the chunk continues. A drawing that hangs
REM  is killed at AEC_HARVEST_TIMEOUT and marked .fail. A killed process resumes:
REM  finished drawings are skipped, not redone.
REM
REM  Usage (flags can be combined):
REM     RunPipeline.bat              harvest every DWG directly in Input_DWGs
REM     RunPipeline.bat /r           ... including sub-folders
REM     RunPipeline.bat /append      keep finished results, harvest only what is missing
REM     RunPipeline.bat /new         rebuild the DLL first
REM     RunPipeline.bat /q           do not pause at the end
REM
REM  Exit codes: 0 = every drawing succeeded, 2 = at least one failed, 1 = aborted.
REM ===========================================================================

set "ROOT=%~dp0"
if "%ROOT:~-1%"=="\" set "ROOT=%ROOT:~0,-1%"

set "PROJECT=%ROOT%\AEC_Batch_Data_Haravester"
set "SOLUTION=%ROOT%\AEC_Batch_Data_Haravester.slnx"
set "INPUT=%ROOT%\Input_DWGs"
set "OUTPUT=%ROOT%\Output"
set "LOGS=%ROOT%\Logs"
set "SCRIPTS=%ROOT%\Scripts"
set "SCRIPT=%SCRIPTS%\HarvestBatch.scr"
set "CSV=%OUTPUT%\Extracted_BOM.csv"
set "ATTRCSV=%OUTPUT%\Extracted_Attributes.csv"
set "MANIFEST=%OUTPUT%\manifest.txt"
set "ROWS=%OUTPUT%\_rows"
set "STATUSDIR=%OUTPUT%\_status"
set "DLLNAME=AEC_Batch_Data_Haravester.dll"

REM Per-chunk wall-clock limit. A drawing that never returns must not stall the batch;
REM the supervisor kills the process and marks that drawing .fail. Override from the
REM environment for a folder of unusually large drawings.
if not defined AEC_HARVEST_TIMEOUT set "AEC_HARVEST_TIMEOUT=300"
REM Drawings per Core Console process. One process amortises ~1.6 s of AutoCAD startup
REM across the chunk; the chunk size bounds how much a hard crash can cost.
if not defined AEC_HARVEST_CHUNK set "AEC_HARVEST_CHUNK=50"
REM Chunk processes running at once. Each one is a full AutoCAD instance, so this is 1 by
REM default; raise it only where there is memory to spare and many drawings to harvest.
if not defined AEC_HARVEST_PARALLEL set "AEC_HARVEST_PARALLEL=1"

title AEC Batch Data Harvester

REM ---------------------------------------------------------------- arguments
set "RECURSE="
set "RECURSE_ARG="
set "FRESH=1"
set "BUILD="
set "NOPAUSE="
for %%A in (%*) do (
    if /i "%%~A"=="/r"      set "RECURSE=/s"
    if /i "%%~A"=="/r"      set "RECURSE_ARG=-Recurse"
    if /i "%%~A"=="/append" set "FRESH="
    if /i "%%~A"=="/new"    set "BUILD=1"
    if /i "%%~A"=="/q"      set "NOPAUSE=1"
    if /i "%%~A"=="/?"      goto :usage
    if /i "%%~A"=="-h"      goto :usage
    if /i "%%~A"=="--help"  goto :usage
)

REM ------------------------------------------------------- locate Core Console
set "CONSOLE=C:\Program Files\Autodesk\AutoCAD 2026\accoreconsole.exe"
if not exist "%CONSOLE%" (
    set "CONSOLE="
    for /f "delims=" %%C in ('where /r "C:\Program Files\Autodesk" accoreconsole.exe 2^>nul') do (
        if not defined CONSOLE set "CONSOLE=%%C"
    )
)
if not defined CONSOLE (
    echo [FATAL] accoreconsole.exe not found.
    echo         Expected: C:\Program Files\Autodesk\AutoCAD 2026\accoreconsole.exe
    goto :fail
)

if not exist "%INPUT%" (
    echo [FATAL] Input folder missing: "%INPUT%"
    goto :fail
)

if not exist "%OUTPUT%" mkdir "%OUTPUT%"
if not exist "%LOGS%"   mkdir "%LOGS%"
if not exist "%SCRIPTS%" mkdir "%SCRIPTS%"

REM ----------------------------------------------------------- optional rebuild
if defined BUILD (
    echo [1/4] Building %SOLUTION% ...
    dotnet build "%SOLUTION%" -v q --nologo > "%LOGS%\build.log" 2>&1
    if errorlevel 1 (
        echo [FATAL] Build failed. See "%LOGS%\build.log"
        goto :fail
    )
    echo       Build OK.
)

REM --------------------------------------------------------------- find the DLL
set "DLL="
if exist "%PROJECT%\bin" (
    for /f "delims=" %%D in ('dir /b /s /a-d /o-d "%PROJECT%\bin\%DLLNAME%" 2^>nul') do (
        if not defined DLL set "DLL=%%D"
    )
)
if not defined DLL (
    echo [FATAL] %DLLNAME% not found under "%PROJECT%\bin".
    echo         Build the solution first, or run:  RunPipeline.bat /new
    goto :fail
)

REM --------------------------------------------- regenerate the .scr for this DLL
REM Keeping the script generated means the NETLOAD path can never go stale.
REM NOTE: no trailing blank line. A blank line is an ENTER at the Command prompt,
REM which repeats the previous command (HarvestBatch would run twice). The batch
REM manifest arrives in AEC_HARVEST_BATCH; the script needs no drawing (/i) because
REM the engine opens each one as a side database.
(
    echo ; AUTO-GENERATED by RunPipeline.bat on %DATE% %TIME% - do not edit.
    echo SECURELOAD 0
    echo NETLOAD "%DLL%"
    echo HarvestBatch
) > "%SCRIPT%"

REM A fresh run replaces results; /append keeps the per-drawing files *and* their verdicts,
REM which is what makes a resume possible: a drawing whose .ok, .csv and .attributes.csv
REM all survive is known finished and is not harvested again. A .fail is retried, because
REM /append is also the "carry on after a crash" mode.
if defined FRESH if exist "%CSV%" del /q "%CSV%"
if defined FRESH if exist "%ATTRCSV%" del /q "%ATTRCSV%"
if defined FRESH if exist "%ROWS%" rd /s /q "%ROWS%"
if defined FRESH if exist "%STATUSDIR%" rd /s /q "%STATUSDIR%"

REM The assembly reads these; nothing about output location is hardcoded in C#.
set "AEC_HARVEST_OUTPUT=%OUTPUT%"
set "AEC_HARVEST_ROOT=%ROOT%"

REM ------------------------------------------------------------- reset counters
set /a TOTAL=0
set /a OK=0
set /a FAILED=0
set /a NORESULT=0

REM The supervisor owns the status key. One line per drawing: key|full path.
powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%\tools\build-manifest.ps1" -Root "%ROOT%" -Destination "%MANIFEST%" %RECURSE_ARG%
if errorlevel 1 (
    echo [FATAL] Could not build the manifest.
    goto :fail
)

echo.
echo Console : %CONSOLE%
echo Assembly: %DLL%
echo Input   : %INPUT%%RECURSE%
echo Output  : %CSV%
echo Attributes: %ATTRCSV%
echo Rows    : %ROWS%
if defined FRESH (echo Mode    : fresh - per-drawing files reset) else (echo Mode    : append - finished drawings are kept, not re-harvested)
echo Timeout : %AEC_HARVEST_TIMEOUT% s per chunk, %AEC_HARVEST_CHUNK% drawings per process, %AEC_HARVEST_PARALLEL% at once
echo ---------------------------------------------------------------------------

REM ---------------------------------------------------------------- the engine
REM One process harvests a chunk of drawings as side databases. The helper owns the
REM process boundary: timeout, kill, and the .fail for a drawing that was in flight.
set "BATCHRC=0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%\tools\run-harvest-batch.ps1" ^
    -Root "%ROOT%" -Manifest "%MANIFEST%" -Script "%SCRIPT%" -Console "%CONSOLE%" ^
    -TimeoutSeconds %AEC_HARVEST_TIMEOUT% -ChunkSize %AEC_HARVEST_CHUNK% -Parallel %AEC_HARVEST_PARALLEL%
set "BATCHRC=%ERRORLEVEL%"

REM Counts come from the status files, which are the only authoritative verdict.
set "TOTAL=0"
for /f %%N in ('find /c /v "" ^< "%MANIFEST%"') do set "TOTAL=%%N"
set "OK=0"
for /f %%N in ('dir /b "%STATUSDIR%\*.ok" 2^>nul ^| find /c /v ""') do set "OK=%%N"
set /a FAILED=TOTAL-OK

echo ---------------------------------------------------------------------------
echo DWGs processed : %TOTAL%
echo Succeeded      : %OK%
echo Failed         : %FAILED%

if %TOTAL%==0 (
    echo [WARN] No .dwg files found in "%INPUT%" - drop some test drawings in there.
    goto :summary
)

REM Only drawings that reported .ok are merged: a row file without a verdict is not data.
powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%\tools\merge-rows.ps1" -Root "%ROOT%"
if errorlevel 1 (
    echo [FATAL] Merge failed.
    goto :fail
)

:summary
if exist "%CSV%" (
    for /f %%N in ('find /c /v "" ^< "%CSV%"') do echo CSV lines      : %%N
    echo CSV file       : %CSV%
) else (
    echo [WARN] No CSV was produced.
)
if exist "%ATTRCSV%" (
    for /f %%N in ('find /c /v "" ^< "%ATTRCSV%"') do echo Attribute lines: %%N
    echo Attribute file : %ATTRCSV%
)

if %FAILED% GTR 0 (
    echo [WARN] %FAILED% drawing^(s^) failed.
    echo        Per-drawing reason : "%STATUSDIR%\^<key^>.error.txt"
    echo        Batch-level reason : "%STATUSDIR%\_harvest*.error.txt"
    echo        Process log        : "%LOGS%\batch-*.log"
)

echo.
echo Done.
if not defined NOPAUSE pause

REM A non-zero exit is the only signal a caller (scheduled task, CI, another script)
REM can see. Without this, a run where every drawing failed still looks successful.
REM BATCHRC and the count come from the same status files, so they cannot disagree.
if %BATCHRC% NEQ 0 exit /b 2
if %FAILED% GTR 0 exit /b 2
exit /b 0

REM ===========================================================================
:usage
echo.
echo RunPipeline.bat  -  batch DWG data harvest
echo.
echo   /r        include sub-folders of Input_DWGs
echo   /append   keep finished per-drawing results and harvest only what is missing
echo   /new      rebuild the DLL before harvesting
echo   /q        do not pause at the end
echo.
echo   AEC_HARVEST_TIMEOUT  seconds per chunk before the process is killed (default 300)
echo   AEC_HARVEST_CHUNK    drawings per Core Console process (default 50)
echo   AEC_HARVEST_PARALLEL chunk processes at once (default 1, cap 16)
echo   AEC_HARVEST_EXCEL_SAFE=1   prefix text that starts with = + - @ or a tab with an
echo                              apostrophe, so Excel does not treat it as a formula
echo                              (off by default: it changes real layer names)
echo   AEC_HARVEST_UTF8_BOM=1     write a UTF-8 BOM so Windows Excel decodes non-ASCII
echo                              text correctly (off by default: a BOM confuses parsers)
echo.
exit /b 0

:fail
echo.
echo Aborted.
if not defined NOPAUSE pause
exit /b 1
