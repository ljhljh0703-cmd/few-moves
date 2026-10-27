FROM mcr.microsoft.com/dotnet/sdk:10.0.203 AS build
WORKDIR /src

COPY global.json ./
COPY Assets/Nectorial/SlideCore/ ./Assets/Nectorial/SlideCore/
COPY Assets/Nectorial/CoopCore/ ./Assets/Nectorial/CoopCore/
COPY Assets/Nectorial/RaidCore/ ./Assets/Nectorial/RaidCore/
COPY Assets/Nectorial/RecordCore/ ./Assets/Nectorial/RecordCore/
COPY Assets/Nectorial/Resources/CoopRooms/coop-c1.json Assets/Nectorial/Resources/CoopRooms/coop-c2.json Assets/Nectorial/Resources/CoopRooms/coop-c3.json ./Assets/Nectorial/Resources/CoopRooms/
COPY servers/FewMoves.Coop/ ./servers/FewMoves.Coop/

RUN dotnet publish servers/FewMoves.Coop/FewMoves.Coop.csproj --configuration Release --output /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.3 AS runtime
WORKDIR /app
COPY --from=build /out/ ./

ENV FEW_MOVES_LISTEN_URL=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "FewMoves.Coop.dll"]
