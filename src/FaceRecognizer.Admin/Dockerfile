# Bygges fra rotmappen: docker build -f src/FaceRecognizer.Admin/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/FaceRecognizer.Contracts/*.csproj src/FaceRecognizer.Contracts/
COPY src/FaceRecognizer.Admin/*.csproj src/FaceRecognizer.Admin/
RUN dotnet restore src/FaceRecognizer.Admin
COPY src/FaceRecognizer.Contracts/ src/FaceRecognizer.Contracts/
COPY src/FaceRecognizer.Admin/ src/FaceRecognizer.Admin/
RUN dotnet publish src/FaceRecognizer.Admin -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "FaceRecognizer.Admin.dll"]
