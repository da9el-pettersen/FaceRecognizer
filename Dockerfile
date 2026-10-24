FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY FaceRecognizer.slnx .
COPY src/FaceRecognizer.Contracts/*.csproj src/FaceRecognizer.Contracts/
COPY src/FaceRecognizer.Recognition/*.csproj src/FaceRecognizer.Recognition/
RUN dotnet restore src/FaceRecognizer.Recognition
COPY src/ src/
RUN dotnet publish src/FaceRecognizer.Recognition -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "FaceRecognizer.Recognition.dll"]
