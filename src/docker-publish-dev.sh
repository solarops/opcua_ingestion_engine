#!/bin/bash

echo "* PUBLISHING "

# Set target tag
targetTag="dev"

# Get the current date in the desired format
date=$(date +"%Y-%m-%d_%H-%M")

# Set base tag for the Docker image
tagBase="ghcr.io/solarops/opcua_ingestion_engine/opcua:"

# Combine base tag with date to create the full date tag
dateTag="${tagBase}${date}"

echo "* Docker tag: $dateTag"

# Build the Docker image with the specified tag
docker build -t "$dateTag" -f ./Dockerfile .

echo "* Docker push..."
# Push the Docker image to the registry
docker push "$dateTag"

# Set the final tag
finalTag="${tagBase}${targetTag}"

echo "* Adding $targetTag tag..."
# Tag the image with the final tag
docker tag "$dateTag" "$finalTag"

echo "* Pushing $targetTag image..."
# Push the Docker image with the final tag
docker push "$finalTag"

echo "* Done"