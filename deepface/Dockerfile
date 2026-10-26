# Tynn innpakning rundt det offisielle DeepFace-imaget (serengil/deepface, MIT), låst på digest (v0.0.103).
# Modellvektene bakes inn med sjekksum, slik at containeren ikke trenger internett ved kjøring.
FROM serengil/deepface@sha256:1db249fc8c16e2bef0b42cbe8cc0ca95a59c970ec0cc01ea457ac879bee50e87

ENV DEEPFACE_HOME=/opt/deepface
# Kun modellene vi har bakt inn forhåndslastes (containeren har ikke internett).
ENV DEEPFACE_FACE_RECOGNITION_MODELS=SFace
ENV DEEPFACE_FACE_DETECTION_MODELS=yunet
# Standard 0.9 er for streng: ansikter på dørkamera (liten andel av 4K-bildet, IR) scorer ofte 0.90-0.93.
ENV yunet_score_threshold=0.6

# SFace (gjenkjenning, Apache-2.0) og YuNet (ansiktsdeteksjon, MIT), begge fra OpenCV Zoo.
ADD --checksum=sha256:0ba9fbfa01b5270c96627c4ef784da859931e02f04419c829e83484087c34e79 \
    https://github.com/opencv/opencv_zoo/raw/main/models/face_recognition_sface/face_recognition_sface_2021dec.onnx \
    /opt/deepface/.deepface/weights/face_recognition_sface_2021dec.onnx
ADD --checksum=sha256:8f2383e4dd3cfbb4553ea8718107fc0423210dc964f9f4280604804ed2552fa4 \
    https://github.com/opencv/opencv_zoo/raw/main/models/face_detection_yunet/face_detection_yunet_2023mar.onnx \
    /opt/deepface/.deepface/weights/face_detection_yunet_2023mar.onnx

COPY run.sh /run.sh
RUN sed -i 's/\r$//' /run.sh
ENTRYPOINT ["sh", "/run.sh"]
