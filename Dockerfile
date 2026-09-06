FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY BlackoutWatch.csproj .
RUN dotnet restore BlackoutWatch.csproj
COPY . .
RUN dotnet publish BlackoutWatch.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
RUN mkdir -p /data && chown $APP_UID:$APP_UID /data
VOLUME ["/data"]
USER $APP_UID
ENTRYPOINT ["dotnet", "BlackoutWatch.dll"]
