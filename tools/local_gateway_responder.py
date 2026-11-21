#!/usr/bin/env python3
"""Bounded G3 gateway acknowledgements through a user-supplied external MQTT broker."""
import argparse, json, re, time
import paho.mqtt.client as mqtt

def main():
    p=argparse.ArgumentParser()
    p.add_argument('--device',required=True);p.add_argument('--gateway',required=True)
    p.add_argument('--host',required=True);p.add_argument('--port',type=int,required=True)
    p.add_argument('--cafile',required=True);p.add_argument('--username',default='')
    p.add_argument('--password',default='');p.add_argument('--seconds',type=int,default=600)
    p.add_argument('--response-delay',type=float,default=1.5)
    p.add_argument('--weather-code',type=int,choices=range(256),default=1)
    p.add_argument('--empty-gateway',action='store_true')
    a=p.parse_args()
    for value in (a.device,a.gateway):
        if not re.fullmatch(r'[0-9A-Fa-f]{12}',value): p.error('MAC must contain 12 hex digits')
    if not 1<=a.seconds<=3600 or not 0<=a.response_delay<=5 or not 1<=a.port<=65535: p.error('invalid bounds')
    root=f'SENSOR/{a.device.upper()}'
    routes={
      f'{root}/mqtt/event/upload_snapshot':f'{root}/mqtt/event_response/snapshot_upload',
      f'{root}/mqtt/event/get_weather':f'{root}/mqtt/event_response/get_weather',
      f'{root}/IPBOX/{a.gateway.upper()}/mqtt/event/login_ipbox':f'{root}/IPBOX/{a.gateway.upper()}/mqtt/event_response/login_ipbox'}
    if a.empty_gateway:
      gr=f'IPBOX/{a.gateway.upper()}/{root}/mqtt'
      for name in ('get_filter_sensor3','get_error_sensor3'): routes[f'{gr}/service/{name}']=f'{gr}/service_response/{name}'
    client=mqtt.Client(mqtt.CallbackAPIVersion.VERSION2,client_id='g3-compat-ack')
    if a.username: client.username_pw_set(a.username,a.password)
    client.tls_set(ca_certs=a.cafile)
    deadline=time.monotonic()+a.seconds
    def on_connect(c,u,flags,reason,props):
      if reason.is_failure: raise RuntimeError(f'MQTT connect failed: {reason}')
      for topic in routes: c.subscribe(topic,qos=1)
      print(json.dumps({'event':'connected','host':a.host,'port':a.port}),flush=True)
    def on_message(c,u,msg):
      if time.monotonic()>deadline or msg.topic not in routes or len(msg.payload)>8192:return
      try:
        request=json.loads(msg.payload);token=request['client_token'];data=request.get('data',{})
        if not isinstance(token,str) or not 1<=len(token)<=128 or not isinstance(data,dict):return
        if msg.topic.endswith('upload_snapshot') and data.get('physics',{}).get('mac')!=a.device.upper():return
        if not msg.topic.endswith('upload_snapshot') and data.get('terminal_mac',a.gateway.upper())!=a.gateway.upper():return
      except (ValueError,KeyError,TypeError,AttributeError):return
      response={}
      if msg.topic.endswith('/get_weather'): response={'temperature':-100,'humidity':200,'pm25':-100,'weatherCode':a.weather_code}
      elif a.empty_gateway and msg.topic.endswith('/get_filter_sensor3'):response={'filter_infos':[]}
      elif a.empty_gateway and msg.topic.endswith('/get_error_sensor3'):response={'error_infos':[]}
      body=json.dumps({'code':0,'data':response,'client_token':token,'timestamp':int(time.time()*1000)},separators=(',',':'))
      time.sleep(a.response_delay)
      c.publish(routes[msg.topic],body,qos=1,retain=False)
      print(json.dumps({'event':'compat-ack','topic':routes[msg.topic]}),flush=True)
    client.on_connect=on_connect;client.on_message=on_message
    client.connect(a.host,a.port,keepalive=30);client.loop_start()
    try:
      while time.monotonic()<deadline and client.is_connected():time.sleep(.25)
    finally:
      client.loop_stop();client.disconnect()
if __name__=='__main__':main()
