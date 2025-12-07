#!/bin/bash

# Get script directory to ensure relative paths work
DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
cd "$DIR"

function stop_backend {
    echo "--- Stopping Backend (Port 5098) ---"
    PID=$(lsof -ti:5098)
    if [ ! -z "$PID" ]; then
        kill -9 $PID
        echo "Killed Backend PID: $PID"
    else
        echo "No backend running on port 5098."
    fi
}

function start_backend {
    echo "--- Starting Backend ---"
    cd backend
    dotnet run
}

function stop_frontend {
    echo "--- Stopping Frontend (Vite) ---"
    # Find processes running vite
    PIDS=$(pgrep -f "vite")
    if [ ! -z "$PIDS" ]; then
        echo "$PIDS" | xargs kill -9
        echo "Killed Frontend PIDs: $PIDS"
    else
        echo "No frontend (vite) running."
    fi
}

function start_frontend {
    echo "--- Starting Frontend ---"
    cd frontend
    npm run dev
}

if [ "$1" == "backend" ]; then
    stop_backend
    start_backend
elif [ "$1" == "frontend" ]; then
    stop_frontend
    start_frontend
elif [ "$1" == "stop-all" ]; then
    stop_backend
    stop_frontend
else
    echo "Usage: ./restart.sh [backend|frontend|stop-all]"
    echo ""
    echo "Examples:"
    echo "  ./restart.sh backend   # Restarts .NET Backend"
    echo "  ./restart.sh frontend  # Restarts Vite Frontend"
    echo "  ./restart.sh stop-all  # Kills both services"
fi
