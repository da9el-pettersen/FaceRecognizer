FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY FaceRecognizer.slnx .
COPY src/FaceRecognizer.Contracts/*.csproj src/FaceRecognizer.Contracts/
COPY src/FaceRecognizer.Recognition/*.csproj src/FaceRecognizer.Recognition/
RUN dotnet restore src/FaceRecognizer.Recognition
COPY src/ src/
# Lisensnøkkel for SixLabors.ImageSharp 4.x (påkrevd for Release-bygg)
ARG SIXLABORS_LICENSE_KEY=""
RUN dotnet publish src/FaceRecognizer.Recognition -c Release -o /app -p:SixLaborsLicenseKey=$SIXLABORS_LICENSE_KEY

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "FaceRecognizer.Recognition.dll"]
