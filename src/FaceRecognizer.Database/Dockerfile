# Bygges fra rotmappen: docker build -f src/FaceRecognizer.Database/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/FaceRecognizer.Contracts/*.csproj src/FaceRecognizer.Contracts/
COPY src/FaceRecognizer.Database/*.csproj src/FaceRecognizer.Database/
RUN dotnet restore src/FaceRecognizer.Database
COPY src/FaceRecognizer.Contracts/ src/FaceRecognizer.Contracts/
COPY src/FaceRecognizer.Database/ src/FaceRecognizer.Database/
RUN dotnet publish src/FaceRecognizer.Database -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080 Database__Path=/data/faces.db
VOLUME /data
ENTRYPOINT ["dotnet", "FaceRecognizer.Database.dll"]
