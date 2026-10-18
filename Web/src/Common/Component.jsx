import React from 'react'
import { Button, Form, Table, Pagination, Toast, Dropdown, SplitButtonGroup, Spin, Modal, Typography, Tag } from '@douyinfe/semi-ui';
import { IconUser, IconTreeTriangleDown } from '@douyinfe/semi-icons';
import { getGuid } from './Common';

class ContentPage extends React.Component {
    constructor(props) {
        super(props);
    }
    render() {
        return <div className={this.props.loading ? 'contentPage loading' : 'contentPage'}>
            {this.props.loading ? <Spin size="large" /> : this.props.children}
        </div>
    }
}
class UIModal extends React.Component {
    constructor(props) {
        super(props);
        this.state = { show: false, confirmLoading: false };
    }
    show = () => {
        this.setState({ show: true });
    }
    hide = () => {
        this.setState({ show: false });
    }
    onOk = () => {
        if (this.onSubmit) {
            if (this.onSubmit.constructor.name == 'AsyncFunction') {
                this.setState({ confirmLoading: true }, () => {
                    this.onSubmit().then(() => {
                        this.setState({ confirmLoading: false });
                    });
                });
            }
            else {
                this.setState({ confirmLoading: true }, () => {
                    this.onSubmit();
                    this.setState({ confirmLoading: false });
                });
            }
        }
    }
    render() {
        return <Modal title={this.props.title ?? '编辑'} width={this.props.width ?? 360} confirmLoading={this.state.confirmLoading} centered visible={this.state.show} onOk={this.onOk} onCancel={this.hide}>
            {this.children()}
        </Modal>
    }
}
const ToastUI = {
    info: (msg) => Toast.info({ content: msg, top: '75%', stack: true, showClose: false }),
    error: (msg) => Toast.error({ content: msg, top: '75%', stack: true, showClose: false }),
    warning: (msg) => Toast.warning({ content: msg, top: '75%', stack: true, showClose: false }),
    success: (msg) => Toast.success({ content: msg, top: '75%', stack: true, showClose: false })
}
export { ToastUI, ContentPage, UIModal }