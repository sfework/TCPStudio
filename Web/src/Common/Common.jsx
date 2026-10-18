import { useNavigate, useLocation, useParams } from "react-router-dom";
function withRouter(WrapperComponent) {
    return function (props) {
        var location = useLocation();
        var navigate = useNavigate();
        var _searchParams = {};
        for (let p of new URLSearchParams(location.search)) {
            _searchParams[p[0]] = p[1];
        }
        var router = {
            location: location,
            state: location.state,
            back: () => {
                window.history.back();
            },
            naviagteTo: (url, state) => {
                if (state) {
                    navigate(url, { state: state });
                }
                else {
                    navigate(url);
                }
            },
            naviagte: (url, state) => {
                return () => {
                    if (state) {
                        navigate(url, { state: state });
                    }
                    else {
                        navigate(url);
                    }
                };
            },
            params: useParams(),
            query: _searchParams
        }
        var _props = { ...props, ...router }
        return <WrapperComponent {..._props} />
    };
}
function getGuid() {
    return "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx".replace(/[x]/g, () =>
        Math.floor(Math.random() * 16)
            .toString(16)
            .toUpperCase()
    );
}
class InvokerFunction {
    constructor(inv) {
        this.inv = inv;
        return new Proxy(this, {
            get(target, className) {
                return target.createClassProxy(className);
            }
        })
    }
    createClassProxy = (className) => {
        return new Proxy(this, {
            get(target, methodName) {
                return async function (...args) {
                    return await target.call(className, methodName, ...args);
                };
            }
        });
    }
    call = async (className, methodName, args) => {
        args = this.convertToString(args);
        return this.convertToResult(await this.inv[className].InvokeCall(methodName, args));
    }
    convertToString = (val) => {
        if (!val) { return null; }
        var _type = Object.prototype.toString.call(val);
        if (_type === '[object Object]' || _type === '[object Array]') {
            return JSON.stringify(val);
        }
        else {
            return val.toString();
        }
    }
    convertToResult = (str) => {
        if (typeof str !== 'string' || !str.trim()) {
            return str;
        }
        const trimmedStr = str.trim();
        if (trimmedStr.startsWith('{') || trimmedStr.startsWith('[')) {
            try {
                const jsonObj = JSON.parse(trimmedStr);
                return jsonObj;
            } catch (e) {

            }
        }
        const numberRegex = /^-?(\d+\.?\d*|\.\d+)([eE][-+]?\d+)?$/;
        if (numberRegex.test(trimmedStr)) {
            const num = Number(trimmedStr);
            if (Number.isFinite(num)) {
                return num;
            }
        }
        return str;
    }
}
const InvProxy = new InvokerFunction(window.chrome?.webview?.hostObjects ?? {});

/* ============ 宿主 → 前端 订阅总入口（通用桥接） ============
 * C# HostBridge 经 ExecuteScriptWithResultAsync 调用 window.hostRpc.onHostAction(action, data)，
 * 按 action 路由到 host.bind 注册的处理函数；其返回值（含 Promise）即后端 CallAsync<T> 的结果。
 */
window.hostRpc = window.hostRpc ?? {};

class HostActionBind {
    constructor() {
        this.hostActions = {};
        // 接线到宿主桥：按 action 分发，未绑定显式报错，避免后端拼错名字时静默丢失
        window.hostRpc.onHostAction = async (action, data) => {
            const fn = this.hostActions[action];
            if (!fn) {
                console.warn(`[host] 未绑定的 action: ${action}`);
                throw new Error(`未绑定的 action: ${action}`);
            }
            // 必须 return：后端 CallAsync<T> 依赖此返回值
            return await fn(data);
        };
    }
    bind = (action, fn) => {
        this.hostActions[action] = fn;
    }
    /** 解绑一个或多个 action：unBind('a') 或 unBind('a','b') */
    unBind = (...actions) => {
        actions.forEach(a => delete this.hostActions[a]);
    }
}

var host = new HostActionBind();

export { withRouter, getGuid, InvProxy, host }