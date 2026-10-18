import { InvProxy } from './Common';

/* TCP 业务服务代理：方法签名与后端 TcpService 一致（单参数、强类型对象直传，桥接层自动序列化） */
const Tcp = InvProxy.Tcp;

export { Tcp };
