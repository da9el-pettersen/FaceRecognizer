FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY FaceRecognizer.slnx .
COPY src/FaceRecognizer.Contracts/*.csproj src/FaceRecognizer.Contracts/
COPY src/FaceRecognizer.Recognition/*.csproj src/FaceRecognizer.Recognition/
RUN dotnet restore src/FaceRecognizer.Recognition
COPY src/ src/
# Valgfri lisensfil for SixLabors.ImageSharp 4.x (påkrevd for Release-bygg). Ligger kun lokalt, ikke i git.
COPY sixlabors.li[c] ./
RUN if [ -f sixlabors.lic ]; then LIC="-p:SixLaborsLicenseFile=/src/sixlabors.lic"; fi; \
    dotnet publish src/FaceRecognizer.Recognition -c Release -o /app $LIC

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "FaceRecognizer.Recognition.dll"]
