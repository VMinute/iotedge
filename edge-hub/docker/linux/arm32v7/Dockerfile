ARG base_tag=1.0.0-preview010-linux-arm32v7
FROM azureiotedge/azureiotedge-hub-base:${base_tag}

ARG EXE_DIR=.

WORKDIR /app

COPY $EXE_DIR/ ./

# Expose MQTT, AMQP and HTTPS ports
EXPOSE 8883/tcp
EXPOSE 5671/tcp
EXPOSE 443/tcp

USER edgehubuser

CMD echo "$(date --utc +"[%Y-%m-%d %H:%M:%S %:z]"): Starting Edge Hub" && \
    exec /usr/bin/dotnet Microsoft.Azure.Devices.Edge.Hub.Service.dll
