# EMQX Learning

## Performance test
+ To start benchmark test
```sh
./custom_eqmtt_bench.sh 5 -c 1 -I 1000 -q 1 -m '{"messageId":1,"temperature":32.81665161616013,"humidity":71.98951628617453,"deviceId":"DB","timestamp":1679898067325,"ack":true,"snr":9,"txt":"text"}'
```