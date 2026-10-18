import React from 'react'
import { Button, Dropdown, Empty, Form, Modal, Space, Tag, Toast, Typography } from '@douyinfe/semi-ui';
import { IconDelete, IconEdit, IconPlus, IconMore } from '@douyinfe/semi-icons';
import { getGuid } from '../../Common/Common';
import { UIModal } from '../../Common/Component';
import { Tcp } from '../../Common/Api';

const { Text } = Typography;

/* 连接状态 -> 展示配置 */
const StatusMap = {
    disconnected: { text: '未连接', color: 'grey' },
    connecting: { text: '连接中', color: 'light-blue' },
    connected: { text: '已连接', color: 'green' },
    error: { text: '异常', color: 'red' },
};
const EncodingOptions = [
    { label: 'HEX', value: 'hex' },
    { label: 'ASCII', value: 'ascii' },
    { label: 'UTF-8', value: 'utf8' },
    { label: 'Base64', value: 'base64' },
];

/* 新增 / 编辑连接弹窗 */
class ConnectionEditModal extends UIModal {
    constructor(props) {
        super(props);
        this.state = { ...this.state, item: null };
        this.formApi = null;
    }
    openEdit = (item) => {
        this.setState({ item: item ?? null, show: true }, () => {
            this.formApi?.reset();
            if (item) this.formApi?.setValues({ ...item });
        });
    }
    children = () => <Form labelAlign='right' getFormApi={(api) => this.formApi = api} initValues={{ encoding: 'hex' }}
        labelPosition="left" labelWidth={60}>
        <Form.Input field="name" label="名称" placeholder="如 设备A-主控" rules={[{ required: true, message: '请输入名称' }]} />
        <Form.Input field="host" label="地址" placeholder="IP 或域名" rules={[{ required: true, message: '请输入地址' }]} />
        <Form.InputNumber style={{ width: '100%' }} field="port" label="端口" min={1} max={65535} rules={[{ required: true, message: '请输入端口' }]} />
        <Form.Select style={{ width: '100%' }} field="encoding" label="编码" optionList={EncodingOptions} />
    </Form>
    onSubmit = async () => {
        let values;
        try {
            values = await this.formApi?.validate();
        } catch (e) {
            return; // 校验失败保持弹窗打开
        }
        // 不再提供自动重连配置，编辑旧数据时一并关闭
        const config = { ...values, autoReconnect: false, id: this.state.item?.id ?? getGuid() };
        if (this.state.item) {
            await Tcp.UpdateConnection(config);
        } else {
            await Tcp.AddConnection(config);
        }
        this.hide();
        this.props.onSaved?.();
    }
}

class ConnectionList extends React.Component {
    constructor(props) {
        super(props);
        this.state = { list: [], loading: false };
        this.editModal = React.createRef();
    }
    componentDidMount() {
        this.reload();
    }
    reload = async () => {
        this.setState({ loading: true });
        let list = this.state.list;
        try {
            list = (await Tcp.GetConnections()) ?? [];
            this.setState({ list });
        } catch (e) {
            Toast.error({ content: `获取连接列表失败：${e.message ?? e}`, top: '75%', stack: true });
        }
        this.setState({ loading: false });
        this.props.onChange?.(list);
    }
    /* 选中某个连接（由父组件控制，便于右侧工作台联动） */
    onSelect = (item) => {
        this.props.onSelect?.(item.id);
    }
    onConnect = async (item) => {
        const result = await Tcp.Connect(item.id);
        if (!result?.ok) {
            Toast.error({ content: result?.msg ?? '连接失败', top: '75%', stack: true });
        }
        // 连接为异步过程，最终 connected/error 由状态事件推送后刷新
        this.reload();
    }
    onDisconnect = async (item) => {
        await Tcp.Disconnect(item.id);
        this.reload();
    }
    onRemove = (item) => {
        Modal.confirm({
            title: '删除连接',
            content: `确定删除「${item.name}」？该连接的收发记录会一并删除。`,
            okText: '删除',
            cancelText: '取消',
            type: 'warning',
            onOk: async () => {
                await Tcp.RemoveConnection(item.id);
                if (this.props.selectedId === item.id) this.props.onSelect?.(null);
                this.reload();
            },
        });
    }
    /* 新增 / 编辑 */
    openEdit = (item) => {
        this.editModal.current?.openEdit(item);
    }
    render() {
        const { list, loading } = this.state;
        return <div className="connPanel">
            <div className="connPanelHead">
                <Text className="connPanelTitle">连接管理</Text>
                <Space>
                    <Button icon={<IconPlus />} type="primary" onClick={() => this.openEdit()}>新建</Button>
                </Space>
            </div>
            <div className="connPanelBody">
                {list.length === 0 && !loading
                    ? <Empty description="暂无连接，点击「新建」添加" />
                    : list.map(item => this.renderItem(item))}
            </div>
            <ConnectionEditModal width={300} ref={this.editModal} onSaved={this.reload} />
        </div>
    }
    renderItem(item) {
        const st = StatusMap[item.status] ?? StatusMap.disconnected;
        const selected = this.props.selectedId === item.id;
        return <div key={item.id} className={selected ? 'connItem selected' : 'connItem'} onClick={() => this.onSelect(item)}>
            <div className="connItemMain">
                <div className="connItemTitle">
                    <Text className="connItemName" ellipsis={{ showTooltip: true }}>{item.name}</Text>
                    <Tag style={{ marginRight: 10 }} size="small" color={st.color}>{st.text}</Tag>
                </div>
                <span className="connItemAddr">{item.host}:{item.port}</span>
            </div>
            <div className="connItemOps" onClick={(e) => e.stopPropagation()}>
                <Dropdown trigger="click" clickToHide render={<>
                    <Dropdown.Item icon={<IconEdit />} onClick={() => this.openEdit(item)}>编辑</Dropdown.Item>
                    <Dropdown.Item icon={<IconDelete />} type="danger" onClick={() => this.onRemove(item)}>删除</Dropdown.Item>
                </>}>
                    <Button size="small" icon={<IconMore />} theme="borderless" />
                </Dropdown>
            </div>
        </div>
    }
}
export default ConnectionList;
