# Start with the official .NET 10.0 SDK image
# Cache the dependencies so we don't have to restore them every time
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS dependencies

# Install Node.js (replace with the latest LTS version)
RUN curl -fsSL https://deb.nodesource.com/setup_24.x | bash - \
    && apt-get install -y nodejs

# Set the working directory to the app's source code directory
WORKDIR /app

# We need python for some reason
RUN apt-get update && apt-get install -y python3
RUN apt-get install -y libatomic1

# Restore the .NET dependencies
FROM dependencies AS dotnet-restore

# Copy the app's source code to the container image
COPY . .

# Restore workloads
RUN dotnet workload restore Valour/Valour.sln

# Restore the app's dependencies
RUN dotnet restore Valour/BuildTools/CssBundler/CssBundler.csproj
RUN dotnet restore Valour/Valour.sln

# Build stage for building/publishing the app
FROM dotnet-restore AS build

# Remove .js files that have corresponding .ts files
# RUN find . -name "*.ts" | while read tsfile; do \
#        jsfile="${tsfile%.ts}.js"; \
#        if [ -f "$jsfile" ]; then \
#            echo "Deleting $jsfile because $tsfile exists"; \
#            rm "$jsfile"; \
#        fi; \
#    done

# Build the app
RUN dotnet publish Valour/Server/Valour.Server.csproj -c Release -o out

# Start with a smaller runtime image for the final image
FROM mcr.microsoft.com/dotnet/aspnet:10.0.3-noble-chiseled-extra AS final

# Set the working directory to the app's output directory
WORKDIR /app

# Copy the app's output files from the build-env image
COPY --from=build /app/out .

# Expose the app's port (if needed)
EXPOSE 80

# Start the app
ENTRYPOINT ["dotnet", "Valour.Server.dll"]
