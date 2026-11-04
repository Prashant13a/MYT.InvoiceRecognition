FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY MYT.InvoiceRecognition.Api/MYT.InvoiceRecognition.Api.csproj MYT.InvoiceRecognition.Api/
RUN dotnet restore MYT.InvoiceRecognition.Api/MYT.InvoiceRecognition.Api.csproj
COPY MYT.InvoiceRecognition.Api/ MYT.InvoiceRecognition.Api/
RUN dotnet publish MYT.InvoiceRecognition.Api/MYT.InvoiceRecognition.Api.csproj -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /out .
ENV ASPNETCORE_URLS=http://+:8000
USER $APP_UID
EXPOSE 8000
ENTRYPOINT ["dotnet", "MYT.InvoiceRecognition.Api.dll"]
