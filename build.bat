dotnet clean
dotnet publish ".\DigitalEye.slnx" -r linux-arm64 -c Release --self-contained false -p:PublishSingleFile=true -p:PublishReadyToRun=true -o ".\dist\publish"