import React from 'react'
import { Button, Dropdown, Empty, Form, Modal, Radio, Space, Switch, Tabs, Tag, Toast } from '@douyinfe/semi-ui';
import { IconDelete, IconEdit, IconLink, IconPlay, IconPlus, IconSend, IconUnlink } from '@douyinfe/semi-icons';
import { getGuid } from '../../Common/Common';
import { Tcp } from '../../Common/Api';

const fmtTime = (ts) => {
    const d = new Date(ts);
    const p = (n, w = 2) => String(n).padStart(w, '0');
    return `${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}.${p(d.getMilliseconds(), 3)}`;
};
/* 单连接工作台：实时收发 / 预设 */
class Workbench extends React.Component {
    constructor(props) {
        super(props);
        this.state = {
            activeTab: 'live',
            connId: props.connection?.id ?? null,
            live: [],            // 实时区消息（内存级，可清空）
            autoScroll: true,
            viewMode: 'raw',     // raw=原始传输内容 / decode=解码（预留编解码插件）
            sendText: '',
            sending: false,
            // 预设 Tab
            presets: [], presetShow: false, presetId: null,
        };
        this.liveRef = null;
        this.presetFormApi = null;
    }
    componentDidMount() {
        this.loadPresets();
    }
    /* 连接切换时重置实时区与记录（纯派生，无副作用，避免 componentDidUpdate 循环） */
    static getDerivedStateFromProps(props, state) {
        const id = props.connection?.id ?? null;
        if (id !== state.connId) {
            return { connId: id, live: [], activeTab: 'live' };
        }
        return null;
    }
    /* 宿主推送入口（由 Home 统一分发） */
    onEvent = (evt) => {
        if (evt.connectionId !== this.props.connection?.id) return;
        if (evt.record) {
            this.setState(s => ({ live: [...s.live, evt.record].slice(-500) }), this.scrollLive);
        }
    }
    scrollLive = () => {
        if (this.state.autoScroll && this.liveRef) {
            this.liveRef.scrollTop = this.liveRef.scrollHeight;
        }
    }
    /* 连接 / 断开（tabBarExtraContent 按钮） */
    onConnect = async () => {
        const conn = this.props.connection;
        const result = await Tcp.Connect(conn.id);
        if (!result?.ok) {
            Toast.error({ content: result?.msg ?? '连接失败', top: '75%', stack: true });
        }
        // 连接为异步过程，最终状态由事件推送后刷新列表
        this.props.onConnectionsChanged?.();
    }
    onDisconnect = async () => {
        await Tcp.Disconnect(this.props.connection.id);
        this.props.onConnectionsChanged?.();
    }
    loadPresets = async () => {
        const list = await Tcp.GetPresets();
        this.setState({ presets: list ?? [] });
    }
    /* 发送：编码固定跟随连接设置 */
    doSend = async (data) => {
        const conn = this.props.connection;
        if (!conn) return;
        this.setState({ sending: true });
        try {
            const res = await Tcp.Send({ id: conn.id, data });
            if (!res?.ok) {
                Toast.error({ content: res?.msg ?? '发送失败', top: '75%', stack: true });
            } else if (res.record) {
                this.setState(s => ({ live: [...s.live, res.record].slice(-500) }), this.scrollLive);
            }
        } catch (e) {
            Toast.error({ content: `发送异常：${e.message ?? e}`, top: '75%', stack: true });
        }
        this.setState({ sending: false });
    }
    onSend = () => {
        const text = this.state.sendText.trim();
        if (!text) { Toast.warning({ content: '请输入发送内容', top: '75%', stack: true }); return; }
        this.doSend(text);
    }
    /* 预设管理 */
    openPreset = (item) => {
        this.setState({ presetShow: true, presetId: item?.id ?? null }, () => {
            this.presetFormApi?.reset();
            if (item) this.presetFormApi?.setValues({ ...item });
        });
    }
    submitPreset = () => {
        this.presetFormApi?.validate().then(async (values) => {
            // 编码/绑定连接不再单独配置：保存时直接绑定当前选中连接，发送编码跟随连接
            await Tcp.SavePreset({ ...values, id: this.state.presetId ?? undefined, connectionId: this.props.connection?.id ?? null });
            this.setState({ presetShow: false });
            this.loadPresets();
        }).catch(() => { });
    }
    removePreset = async (item) => {
        await Tcp.RemovePreset(item.id);
        this.loadPresets();
    }
    render() {
        const conn = this.props.connection;
        if (!conn) return <div className="workPanelPlaceholder">请在左侧选择或新建一个连接</div>;
        return <div className="workbench">
            <div className="workbenchHead">
                <span className="workbenchTitle">{conn.name}</span>
                <span className="workbenchSub">{conn.host}:{conn.port}</span>
            </div>
            <Tabs tabSize="small" type="card" activeKey={this.state.activeTab}
                onChange={(k) => this.setState({ activeTab: k })}
                tabBarExtraContent={this.renderConnButton()}>
                <Tabs.TabPane tab="实时" itemKey="live">{this.renderLive()}</Tabs.TabPane>
                <Tabs.TabPane tab="预设" itemKey="presets">{this.renderPresets()}</Tabs.TabPane>
            </Tabs>
            {this.renderPresetModal()}
        </div>
    }
    renderConnButton() {
        const conn = this.props.connection;
        return conn.status === 'connected'
            ? <Button size="small" type="warning" icon={<IconUnlink />} onClick={this.onDisconnect}>断开</Button>
            : <Button size="small" type="primary" icon={<IconLink />} loading={conn.status === 'connecting'} onClick={this.onConnect}>连接</Button>;
    }
    renderLive() {
        const conn = this.props.connection;
        const { live, viewMode, sendText, sending, autoScroll, presets } = this.state;
        return <div className="livePane">
            <div className="liveToolbar">
                <Radio.Group type="button" size="small" value={viewMode} onChange={(e) => {
                    // 解码依赖后续编解码插件，暂未开放，点击仅提示不切换
                    if (e.target.value === 'decode') {
                        Toast.info({ content: '解码功能未开放', top: '75%', stack: true });
                        return;
                    }
                    this.setState({ viewMode: e.target.value });
                }}>
                    <Radio value="raw">原始</Radio>
                    <Radio value="decode">解码</Radio>
                </Radio.Group>
                <Space>
                    <span className="toolLabel">自动滚动</span>
                    <Switch size="small" checked={autoScroll} onChange={(v) => this.setState({ autoScroll: v })} />
                    <Button size="small" theme="borderless" icon={<IconDelete />} onClick={() => this.setState({ live: [] })}>清空</Button>
                </Space>
            </div>
            <div className="liveArea" ref={(el) => this.liveRef = el}>
                {live.length === 0
                    ? <Empty description="等待数据…" />
                    : live.map(r => <div key={r.id} className={r.direction === 'send' ? 'liveLine send' : 'liveLine recv'}>
                        <div className="liveMeta">
                            <Tag size="small" color={r.direction === 'send' ? 'light-blue' : 'light-green'}>{r.direction === 'send' ? '发送' : '接收'}</Tag>
                            <span className="liveTime">{fmtTime(r.timestamp)}</span>
                        </div>
                        {/* 原始=线路字节 HEX；解码=按编码解码的内容，后续接入编解码插件 */}
                        <div className="liveText">{viewMode === 'raw' ? this.hexOf(r) : r.text}</div>
                    </div>)}
            </div>
            <div className="sendBar">
                <textarea className="sendInput" value={sendText} placeholder="输入发送内容（HEX 支持空格/换行分隔）"
                    onChange={(e) => this.setState({ sendText: e.target.value })}
                    onKeyDown={(e) => { if (e.key === 'Enter' && e.ctrlKey) this.onSend(); }} />
                <div className="sendOps">
                    {/* 预设快捷入口：选择后把预设内容填入发送区；
                        render 需返回元素（Fragment），裸数组 Semi Dropdown 不识别 */}
                    <Dropdown trigger="click" clickToHide render={<>
                        {presets.length === 0
                            ? <Dropdown.Item disabled>暂无预设</Dropdown.Item>
                            : presets.map(p => <Dropdown.Item key={p.id} onClick={() => this.setState({ sendText: p.content })}>{p.name}</Dropdown.Item>)}
                    </>}>
                        <Button size="small" icon={<IconPlay />}>预设</Button>
                    </Dropdown>
                    <Button size="small" type="primary" icon={<IconSend />} loading={sending}
                        disabled={conn.status !== 'connected'} onClick={this.onSend}>发送</Button>
                </div>
            </div>
        </div>
    }
    /* 原始传输内容：把线路字节以 HEX 展示。
       注意：Uint8Array.map 会把回调结果收回字节数组（"AD"→NaN→0），必须先转普通数组 */
    hexOf(r) {
        try {
            const bytes = Uint8Array.from(atob(r.rawBase64), c => c.charCodeAt(0));
            return Array.from(bytes, b => b.toString(16).toUpperCase().padStart(2, '0')).join(' ');
        } catch (e) {
            return r.text;
        }
    }
    renderPresets() {
        const { presets } = this.state;
        return <div className="livePane">
            <div className="liveToolbar">
                <Button size="small" type="primary" icon={<IconPlus />} onClick={() => this.openPreset()}>新建预设</Button>
                <span className="toolLabel">可在实时页签发送区「预设」下拉中填入</span>
            </div>
            <div className="presetList">
                {presets.length === 0
                    ? <Empty description="暂无预设" />
                    : presets.map(p => <div key={p.id} className="presetItem">
                        <div className="presetMain">
                            <span className="connItemName">{p.name}</span>
                            <span className="connItemAddr presetContent" title={p.content}>{p.content}</span>
                        </div>
                        <Space>
                            <Button size="small" theme="borderless" icon={<IconEdit />} onClick={() => this.openPreset(p)} />
                            <Button size="small" theme="borderless" type="danger" icon={<IconDelete />} onClick={() => this.removePreset(p)} />
                        </Space>
                    </div>)}
            </div>
        </div>
    }
    renderPresetModal() {
        const { presetShow, presetId } = this.state;
        return <Modal title={presetId ? '编辑预设' : '新建预设'} visible={presetShow} centered width={420}
            okText="保存" cancelText="取消" onOk={this.submitPreset}
            onCancel={() => this.setState({ presetShow: false })}>
            <Form getFormApi={(api) => this.presetFormApi = api} labelPosition="left" labelWidth={76}>
                <Form.Input field="name" label="名称" rules={[{ required: true, message: '请输入名称' }]} />
                <Form.TextArea field="content" label="内容" rows={3} rules={[{ required: true, message: '请输入数据包内容' }]} />
            </Form>
        </Modal>
    }
}
export default Workbench;
