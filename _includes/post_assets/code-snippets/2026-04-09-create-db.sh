#!/bin/bash

echo "Creating database..."

let result=1

for i in {1..100}; do
    /opt/mssql-tools18/bin/sqlcmd -b -S localhost -U sa -P "$SA_PASSWORD" -Q "IF NOT EXISTS (SELECT * FROM sys.databases WHERE name = '${DB_NAME}') CREATE DATABASE [${DB_NAME}];" -C
    let result=$?
    if [ $result -eq 0 ]; then
        echo "Creating database completed"
        break
    else
        echo "Creating database. Not ready yet..."
        sleep 1
    fi
done
