FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["WebMusic.Backend.csproj", "."]
RUN dotnet restore "WebMusic.Backend.csproj"
COPY . .
RUN dotnet publish "WebMusic.Backend.csproj" -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
EXPOSE 8080
COPY --from=build /app/publish .

# Install FFmpeg for transcoding
RUN apt-get update && apt-get install -y ffmpeg

ENTRYPOINT ["dotnet", "WebMusic.Backend.dll"]
