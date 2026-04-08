FROM mcr.microsoft.com/dotnet/sdk:10.0

WORKDIR /src

COPY MyOptiAlloySite.csproj .
COPY Directory.Build.props .
COPY nuget.config .

RUN dotnet restore

EXPOSE 80 443 5100 5101

ENTRYPOINT ["dotnet", "run", "--no-launch-profile"]
