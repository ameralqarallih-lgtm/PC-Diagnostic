# The problem-report service. Build from the repository root:
#   docker build -f tools/PcDiag.ReportService/Dockerfile -t pcdiag-report-service .
# Run with a volume for the reports and, optionally, Azure DevOps settings:
#   docker run -p 8080:8080 -v reports:/data \
#     -e AzureDevOps__Url=https://dev.azure.com/org -e AzureDevOps__Project=PCDiagnostic -e AzureDevOps__Pat=... \
#     pcdiag-report-service
# Put it behind HTTPS (the product sends reports only over HTTPS) and set its address in the release build
# (installer\build.ps1 -ReportEndpoint https://reports.example.com/).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props global.json* ./
COPY src/PcDiag.Core/ src/PcDiag.Core/
COPY tools/PcDiag.ReportService/ tools/PcDiag.ReportService/
RUN dotnet publish tools/PcDiag.ReportService -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080 Storage__Directory=/data
VOLUME /data
USER app
ENTRYPOINT ["dotnet", "PcDiag.ReportService.dll"]
