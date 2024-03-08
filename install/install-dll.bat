if exist %UserProfile%\AppData\Local\jellyfin\plugins\ (
  FOR /F "eol=| delims=" %%I IN ('DIR "%UserProfile%\AppData\Local\jellyfin\plugins\Intro Skipper*" /B /O-D /TW 2^>nul') DO (
    SET "NewestFile=%UserProfile%\AppData\Local\jellyfin\plugins\%%I"
    GOTO FoundFile
  )
  ECHO Intro Skipper plugin not found!
  GOTO UserInput
)

if exist %ProgramData%\Jellyfin\Server\plugins\ (
  FOR /F "eol=| delims=" %%I IN ('DIR "%ProgramData%\Jellyfin\Server\plugins\Intro Skipper*" /B /O-D /TW 2^>nul') DO (
    SET "NewestFile=%ProgramData%\Jellyfin\Server\plugins\%%I"
    GOTO FoundFile
  )
  ECHO Intro Skipper plugin not found!
  GOTO UserInput
)

ECHO Jellyfin plugin directory not found!
GOTO UserInput

:FoundFile
echo "%NewestFile%"
xcopy /y ConfusedPolarBear.Plugin.IntroSkipper.dll "%NewestFile%"

:UserInput
@pause
