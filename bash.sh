#!/bin/bash -xe
sudo yum update
sudo yum install docker dotnet-sdk-6.0 -y
sudo usermod -aG docker ec2-user
sudo systemctl enable docker
sudo systemctl start docker
sudo docker swarm init