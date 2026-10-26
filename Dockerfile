FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY FaceRecognizer.slnx .
COPY src/FaceRecognizer.Contracts/*.csproj src/FaceRecognizer.Contracts/
COPY src/FaceRecognizer.Recognition/*.csproj src/FaceRecognizer.Recognition/
RUN dotnet restore src/FaceRecognizer.Recognition
COPY src/ src/
RUN dotnet publish src/FaceRecognizer.Recognition -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# Liveness-modeller (MiniFASNet), låst med sjekksum
ADD --checksum=sha256:b32929adc2d9c34b9486f8c4c7bc97c1b69bc0ea9befefc380e4faae4e463907 \
    https://github.com/yakhyo/face-anti-spoofing/releases/download/weights/MiniFASNetV2.onnx /models/MiniFASNetV2.onnx
ADD --checksum=sha256:ebab7f90c7833fbccd46d3a555410e78d969db5438e169b6524be444862b3676 \
    https://github.com/yakhyo/face-anti-spoofing/releases/download/weights/MiniFASNetV1SE.onnx /models/MiniFASNetV1SE.onnx
ENTRYPOINT ["dotnet", "FaceRecognizer.Recognition.dll"]
