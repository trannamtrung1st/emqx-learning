#!/bin/bash

# Check if the host environment variable is set
if [ -z "$HOST" ]; then
    echo "Error: HOST environment variable is not set."
    exit 1
fi

# Check if the number of arguments is provided
if [ "$#" -lt 2 ]; then
    echo "Usage: $0 <number_of_instances> <emqtt_bench_pub_arguments>"
    exit 1
fi

n=$1
shift 1  # Shift the arguments to access only emqtt_bench pub arguments

echo "Starting benchmarking test ..."

for ((i=1; i<=$n; i++)); do
    topic="projectId/${i}/devices/${i}/telemetry"
    emqtt_bench pub -h "$HOST" -t $topic "$@" &
done

wait