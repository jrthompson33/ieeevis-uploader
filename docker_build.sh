#!/bin/bash

# Load the .env file
export $(grep -v '^#' .env | xargs)

#Build the Docker image with the secret keys as build arguments
docker build \
  --build-arg BunnyStorageZoneName=$BunnyStorageZoneName \
  --build-arg BunnyCdnRootUrl=$BunnyCdnRootUrl \
  --build-arg BunnyAccessKey=$BunnyAccessKey \
  --build-arg BunnyBasePath=$BunnyBasePath \
  --build-arg BunnyTokenKey=$BunnyTokenKey \
  --build-arg BunnyUserApiKey=$BunnyUserApiKey \
  --build-arg AuthSignaturePrivateKey=$AuthSignaturePrivateKey \
  --build-arg AwsS3AccessKey=$AwsS3AccessKey \
  --build-arg AwsS3SecretKey=$AwsS3SecretKey \
  --build-arg AwsS3BucketName=$AwsS3BucketName \
  --build-arg AwsS3Region=$AwsS3Region \
  -t ieee-vis-uploader-webapp . 

