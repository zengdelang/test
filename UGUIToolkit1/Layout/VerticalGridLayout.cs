﻿using System;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// 竖直网格布局, 支持ScrollView竖直滑动的时候使用有限的几个item不断复用进行网格布局
/// </summary>
public class VerticalGridLayout : DynamicLayout
{
    protected class RowLayoutParam : LayoutParam
    {
        public int row;         //item所属的行
        public int index;       //item的索引
    }

    [SerializeField] protected int  m_Column = 1;  //布局信息,Item显示的列数
    [SerializeField] protected bool m_ExpandWidth; //强制Item的宽度自适应可视区域的宽,竖直滑动的时候有效

    [SerializeField] protected bool m_AutoDrag; //当content的高度大于viewport的高度时候自动可拖拽，否则不支持拖拽

    [SerializeField] protected bool m_EnableCanLockEvent; //是否开启锁定状态通知，可锁定状态下有新数据来可以提示而不直接刷新ScrollView
    [SerializeField] protected BoolUnityEvent m_CanLockEvent = new BoolUnityEvent(); //可锁定状态指示事件
    [NonSerialized] protected bool m_IsLock; //是否是锁定状态，锁定状态下外部处理不应该在有新数据的时候立即刷新UI，可能会导致频繁的UI刷新，影响UI查看

    [SerializeField] protected bool m_EnableLoadMoreEvent; //是否开启加载更多事件，加载更多可用于分页请求数据
    [SerializeField] protected UnityEvent m_LoadMoreEvent = new UnityEvent(); //加载更多事件
    [SerializeField] protected float m_LoadMoreCD = 0.1f;
    [NonSerialized] protected float m_CurrentTime = -1;

    [SerializeField] protected bool m_EnableArrowHintEvent; //是否开启箭头指示事件, 可选用指示当前viewport向上或向下拖拽是否有内容可拖拽
    [SerializeField] protected BoolUnityEvent m_ArrowUpEvent = new BoolUnityEvent(); //向上箭头指示
    [SerializeField] protected BoolUnityEvent m_ArrowDownEvent = new BoolUnityEvent(); //向下箭头指示

    protected int m_FirstVisibleRow = 0;  //当前区域第一个可见的行, 从0开始
    protected int m_LastVisibleRow = -1;  //当前区域最后一个可见的行
    protected int m_TotalRow = 0;         //可显示的总的行数

    public int column
    {
        get
        {
            return m_Column <= 0 ? 1 : m_Column;
        }
        set
        {
            m_Column = Mathf.Clamp(m_Column, 1, int.MaxValue);
            RefreshAllItem();
        }
    }

    public bool expandWidth
    {
        get { return m_ExpandWidth; }
        set
        {
            m_ExpandWidth = value;
            RefreshCurrentItem();
        }
    }

    public bool autoDrag
    {
        get { return m_AutoDrag; }
        set
        {
            m_AutoDrag = value;
            CheckAutoDrag();
        }
    }

    public bool enableCanLockEvent
    {
        get { return m_EnableCanLockEvent; }
        set { m_EnableCanLockEvent = value; }
    }

    public BoolUnityEvent canLockEvent
    {
        get { return m_CanLockEvent; }
        set { m_CanLockEvent = value; }
    }

    public bool isLock
    {
        get { return m_IsLock; }
        set
        {
            if (m_EnableCanLockEvent)
            {
                if (m_IsLock != value)
                {
                    m_IsLock = value;
                    m_CanLockEvent.Invoke(value);
                    if (!value)
                        RefreshAllItem();
                }
            }
        }
    }

    public bool enableLoadMoreEvent
    {
        get { return m_EnableLoadMoreEvent; }
        set { m_EnableLoadMoreEvent = value; }
    }

    public UnityEvent loadMoreEvent
    {
        get { return m_LoadMoreEvent; }
        set { m_LoadMoreEvent = value; }
    }

    public float loadMoreCD
    {
        get { return m_LoadMoreCD; }
        set { m_LoadMoreCD = value; }
    }

    public bool enableArrowHintEvent
    {
        get { return m_EnableArrowHintEvent; }
        set { m_EnableArrowHintEvent = value; }
    }

    public BoolUnityEvent arrowUpEvent
    {
        get { return m_ArrowUpEvent; }
        set { m_ArrowUpEvent = value; }
    }

    public BoolUnityEvent arrowDownEvent
    {
        get { return m_ArrowDownEvent; }
        set { m_ArrowDownEvent = value; }
    }

    #region Item操作

    /// <summary>
    /// 刷新所有item，布局也从第一个开始重新布局
    /// </summary>
    public override void RefreshAllItem()
    {
        if (m_ScrollView == null || m_Adapter == null)
            return;

        m_FirstVisibleRow = 0;
        m_LastVisibleRow = -1;
        m_ScrollView.content.anchoredPosition = Vector2.zero;
        m_ScrollView.velocity = Vector2.zero;
        RefreshCurrentItem();
    }

    /// <summary>
    /// 刷新当前可见item,通常用于item对应数据被修改，或者有数据被删除时通知刷新UI
    /// </summary>
    public override void RefreshCurrentItem()
    {
        if (m_ScrollView == null || m_Adapter == null)
            return;

        //最好把已有的item全部回收,特别是如果支持多种不同item的不回收，容易复用错误的item
        while (m_ItemViewChildren.Count > 0)
        {
            m_RecycleBin.AddLayoutInfo(m_ItemViewChildren.Dequeue());
        }

        m_FirstVisibleRow = 0;
        m_LastVisibleRow = -1;
        CalculateHeight();
        PerformLayout();
    }

    /// <summary>
    /// 刷新index对应item的UI，item必须是可视的item，否则不进行处理
    /// </summary>
    /// <param name="index"></param>
    public override void RefreshItem(int index)
    {
        if (index < 0 || index >= m_Adapter.GetCount())
            return;

        for (int i = 0, count = m_ItemViewChildren.Count; i < count; ++i)
        {
            var layoutInfo = m_ItemViewChildren.GetElement(i);
            var layoutParam = layoutInfo.layoutParam as RowLayoutParam;
            if (layoutParam.index == index)
            {
                m_Adapter.ProcessItemView(index, layoutInfo.itemView, this);
                break;
            }
        }
    }

    /// <summary>
    /// 定位到某一个item
    /// </summary>
    /// <param name="itemIndex">item在数据中的索引</param>
    /// <param name="resetStartPos">是否从content的开头开始动画滚动</param>
    /// <param name="useAnimation">是否使用动画滚动, false则为瞬间定位到item</param>
    /// <param name="factor">[0, 1] 0显示的viewport的最上面， 1显示的viewport的最下面， 0-1之间按比例显示在viewport中间</param>
    public void ScrollToItem(int itemIndex, bool resetStartPos = false, bool useAnimation = true, float factor = 0.5f)
    {
        if (m_ScrollView == null || m_Adapter == null)
            return;

        if (itemIndex < 0 || itemIndex >= m_Adapter.GetCount())
            return;

        var contentMinPos = 0;
        var contentMaxPos = m_ScrollView.content.rect.height - m_ScrollView.viewport.rect.height;
        if (contentMaxPos < 0)
            contentMaxPos = 0;

        var itemHeight = m_Adapter.GetItemSize().y;
        var rowIndex = itemIndex / column;
        var targetHeight = padding.top + rowIndex * (itemHeight + spacing.y);

        var deltaHeight = m_ScrollView.viewport.rect.height - itemHeight;
        targetHeight -= Mathf.Clamp01(factor) * deltaHeight;
        targetHeight = Mathf.Clamp(targetHeight, contentMinPos, contentMaxPos);

        m_ScrollView.StartAnimation(new Vector2(0, targetHeight), resetStartPos, useAnimation);
    }

    /// <summary>
    /// 检查能否拖拽
    /// </summary>
    protected void CheckAutoDrag()
    {
        if (m_AutoDrag)
        {
            if (m_ScrollView != null)
                m_ScrollView.enabled = m_ScrollView.content.rect.height > (m_ScrollView.viewport.rect.height + 0.2f); //添加0.2f避免相等时候的浮点数误差
        }
        else
        {
            if (m_ScrollView != null)
                m_ScrollView.enabled = true;
        }
    }

    /// <summary>
    /// 检查箭头指示
    /// </summary>
    protected void CheckArrowHint()
    {
        if (m_ScrollView == null)
            return;

        if (m_EnableArrowHintEvent)
        {
            //anchoredPosition.y停留在上方可能存在一些误差,不完全等于0，设置当大于等于0.13有向上箭头指示
            var contentTopY = m_ScrollView.content.anchoredPosition.y;
            m_ArrowUpEvent.Invoke(contentTopY >= 0.13f);

            //计算当前在viewport下边缘处content的Y坐标
            var viewportHeight = m_ScrollView.viewport.rect.height;
            var bottomY = contentTopY + viewportHeight;
            m_ArrowDownEvent.Invoke((viewportHeight - 0.13f) > bottomY);
        }
    }

    /// <summary>
    /// 检查可锁定状态
    /// </summary>
    protected void CheckCanLockState()
    {
        if (m_ScrollView == null)
            return;

        if (m_EnableCanLockEvent)
        {
            if (m_ScrollView.content.rect.height > (m_ScrollView.viewport.rect.height + 0.2f))
            {
                var contentY = m_ScrollView.content.anchoredPosition.y;
                if (m_IsLock && !m_ScrollView.dragging && contentY <= 0.12f)
                {
                    isLock = false;
                    return;
                }

                if (contentY > 0.12f)
                {
                    isLock = true;
                }
            }
        }
    }

    /// <summary>
    /// 检查加载更多处理
    /// </summary>
    protected void CheckLoadMore(bool moveUp)
    {
        if (m_ScrollView == null)
            return;

        if (m_EnableLoadMoreEvent)
        {
            if (moveUp)
            {
                if (m_ScrollView.content.rect.height > (m_ScrollView.viewport.rect.height + 0.2f))
                {
                    //计算当前在viewport下边缘处content的Y坐标
                    var bottomY = m_ScrollView.content.anchoredPosition.y + m_ScrollView.viewport.rect.height;
                    if (m_ScrollView.content.rect.height < bottomY)
                    {
                        if (Time.unscaledTime - m_CurrentTime < m_LoadMoreCD)
                        {
                            return;
                        }
                        m_CurrentTime = Time.unscaledTime;
                        m_LoadMoreEvent.Invoke();
                    }
                }
                else
                {
                    if (Time.unscaledTime - m_CurrentTime < m_LoadMoreCD)
                    {
                        return;
                    }
                    m_CurrentTime = Time.unscaledTime;
                    m_LoadMoreEvent.Invoke();
                }
            }
        }
    }

    #endregion

    #region 布局处理

    /// <summary>
    /// 监听ScrollView的Content位置更改事件, 根据位置重新计算布局
    /// </summary>
    /// <param name="oldPos"></param>
    /// <param name="newPos"></param>
    public override void OnContentPositionChanged(Vector2 oldPos, Vector2 newPos)
    {
        if (m_ScrollView == null || m_Adapter == null || m_Adapter.IsEmpty())
            return;

        if (!m_ScrollView.vertical || m_ScrollView.horizontal)
        {
            Debug.LogError("VerticalGridLayout只支持ScrollView为竖直滑动的时候才生效");
            return;
        }

        PerformLayout();
        CheckArrowHint();
        CheckCanLockState();
        CheckLoadMore(newPos.y > oldPos.y);
    }

    /// <summary>
    /// 计算ScrollView的Content的高度
    /// </summary>
    protected void CalculateHeight()
    {
        if (m_ScrollView == null || m_Adapter == null)
            return;

        var itemCount = m_Adapter.GetCount();
        var itemHeight = m_Adapter.GetItemSize().y;
        m_TotalRow = itemCount / column;
        m_TotalRow += itemCount % column > 0 ? 1 : 0;

        var contentHeight = m_TotalRow * itemHeight + (m_TotalRow - 1) * spacing.y + padding.vertical;
        m_ScrollView.content.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, contentHeight);
        m_ScrollView.content.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, m_ScrollView.viewport.rect.width);

        //计算当前在viewport下边缘处content的Y坐标
        var viewportHeight = m_ScrollView.viewport.rect.height;
        var viewportBottomY = m_ScrollView.content.anchoredPosition.y + viewportHeight;
        if (viewportBottomY > contentHeight)
        {
            var oldPos = m_ScrollView.content.anchoredPosition;
            oldPos.y = Mathf.Max(0, contentHeight - viewportHeight);
            m_ScrollView.content.anchoredPosition = oldPos;
        }

        CheckAutoDrag();
        CheckArrowHint();
        CheckLoadMore(true);
    }

    /// <summary>
    /// 执行布局处理,回收不显示的item, 添加需要显示的item到可视列表中
    /// </summary>
    protected void PerformLayout()
    {
        if (m_ScrollView == null)
            return;

        int newFirstVisibleRow = 0;
        int newLastVisibleRow = 0;
        GetVisibleRowRange(ref newFirstVisibleRow, ref newLastVisibleRow);
        //新的可视行区域和旧的不同时,重新计算需要显示的item
        if (newFirstVisibleRow != m_FirstVisibleRow || newLastVisibleRow != m_LastVisibleRow)
        {
            //如果新的第一个可视行大于旧的,从队列头部移除需要隐藏的item
            if (m_FirstVisibleRow < newFirstVisibleRow)
            {
                while (m_ItemViewChildren.Count > 0)
                {
                    var itemView = m_ItemViewChildren.PeekFirst();
                    var layoutParam = itemView.layoutParam as RowLayoutParam;
                    if (layoutParam.row >= newFirstVisibleRow)
                    {
                        break;
                    }
                    m_RecycleBin.AddLayoutInfo(m_ItemViewChildren.DequeueFirst());
                }
            }

            //如果新的最后一个可视行小于旧的,从队列尾部移除需要隐藏的item
            if (m_LastVisibleRow > newLastVisibleRow)
            {
                while (m_ItemViewChildren.Count > 0)
                {
                    var itemView = m_ItemViewChildren.PeekLast();
                    var layoutParam = itemView.layoutParam as RowLayoutParam;
                    if (layoutParam.row <= newLastVisibleRow)
                    {
                        break;
                    }
                    m_RecycleBin.AddLayoutInfo(m_ItemViewChildren.DequeueLast());
                }
            }

            var columnCount = column;                   //行数,column是个属性提前在这算下,避免循环中多次计算
            var itemCount = m_Adapter.GetCount();       //item的总数,提前在这算下,避免循环中多次计算

            var widthDelta = (m_ScrollView.content.rect.width + spacing.x - padding.horizontal) / columnCount; 
            var heightDelta = m_Adapter.GetItemSize().y + spacing.y;
            var forceItemWidth = widthDelta - spacing.x;

            //如果新的第一个可视行小于旧的,从队列头部添加新的item
            if (m_FirstVisibleRow > newFirstVisibleRow)
            {
                var lastAddItemRow = m_FirstVisibleRow - 1;
                if (newLastVisibleRow < lastAddItemRow)
                {
                    lastAddItemRow = newLastVisibleRow;
                }

                //添加新的item
                for (int i = lastAddItemRow; i >= newFirstVisibleRow; --i)
                {
                    for (int j = 0; j < columnCount; ++j)
                    {
                        var itemIndex = i * columnCount + j;
                        if (itemIndex >= itemCount)
                            continue;

                        var layoutInfo = m_RecycleBin.GetLayoutInfo();
                        if (layoutInfo == null)
                        {
                            layoutInfo = new LayoutInfo();
                            layoutInfo.itemView = m_Adapter.GetItemView(m_ScrollView.content.gameObject);
                            layoutInfo.layoutParam = new RowLayoutParam();
                        }

                        var itemView = layoutInfo.itemView;
                        var layoutParam = layoutInfo.layoutParam as RowLayoutParam;
                        SetItemVisible(itemView.rectTransform, true);
                        if (layoutParam == null)
                            throw new NullReferenceException("layoutParam");
                        layoutParam.row = i;    //把行信息存进去
                        layoutParam.index = itemIndex;
                        m_Adapter.ProcessItemView(itemIndex, itemView, this);
                        m_ItemViewChildren.EnqueueFirst(layoutInfo);
                        SetItemPosition(i, j, itemView.rectTransform, heightDelta, widthDelta, forceItemWidth);
                    }
                }
            }

            //如果新的最后一个可视行大于旧的,从队列尾部添加新的item
            if (m_LastVisibleRow < newLastVisibleRow)
            {
                var firstAddItemRow = m_LastVisibleRow + 1;
                if (newFirstVisibleRow > firstAddItemRow)
                {
                    firstAddItemRow = newFirstVisibleRow;
                }

                //添加新的item
                for (int i = firstAddItemRow; i <= newLastVisibleRow; ++i)
                {
                    for (int j = 0; j < columnCount; ++j)
                    {
                        var itemIndex = i * columnCount + j;
                        if (itemIndex >= itemCount)
                            continue;

                        var layoutInfo = m_RecycleBin.GetLayoutInfo();
                        if (layoutInfo == null)
                        {
                            layoutInfo = new LayoutInfo();
                            layoutInfo.itemView = m_Adapter.GetItemView(m_ScrollView.content.gameObject);
                            layoutInfo.layoutParam = new RowLayoutParam();
                        }

                        var itemView = layoutInfo.itemView;
                        var layoutParam = layoutInfo.layoutParam as RowLayoutParam;
                        SetItemVisible(itemView.rectTransform, true);
                        if (layoutParam == null)
                            throw new NullReferenceException("layoutParam");
                        layoutParam.row = i;    //把行信息存进去
                        layoutParam.index = itemIndex;
                        m_Adapter.ProcessItemView(itemIndex, itemView, this);
                        m_ItemViewChildren.EnqueueLast(layoutInfo);
                        SetItemPosition(i, j, itemView.rectTransform, heightDelta, widthDelta, forceItemWidth);
                    }
                }
            }

            m_FirstVisibleRow = newFirstVisibleRow;
            m_LastVisibleRow = newLastVisibleRow;
        }
    }

    /// <summary>
    /// 获取当前可视的行范围
    /// </summary>
    protected void GetVisibleRowRange(ref int firstVisibleRow, ref int lastVisibleRow)
    {
        //content的上边距离viewport上边的高度
        var deltaHeightToTop = m_ScrollView.content.anchoredPosition.y - padding.top;
        var contentHeight = m_ScrollView.content.rect.height;
        var viewportHeight = m_ScrollView.viewport.rect.height;
        var maxDeltaHeight = contentHeight - viewportHeight;  //达到最下面的时候再向下拖动不改变firstVisibleRow的值
        if (deltaHeightToTop > maxDeltaHeight)
            deltaHeightToTop = maxDeltaHeight;

        var itemHeight = m_Adapter.GetItemSize().y + spacing.y;
        firstVisibleRow = Mathf.FloorToInt(deltaHeightToTop / itemHeight);
        if (firstVisibleRow < 0)
        {
            firstVisibleRow = 0;
        }

        //content的上边距离viewport下边的高度
        var deltaHeightToBottom = viewportHeight + (deltaHeightToTop < 0 ? 0 : deltaHeightToTop);  //如果小于viewport的高度,则设置为viewport的高度
        lastVisibleRow = Mathf.FloorToInt(deltaHeightToBottom / itemHeight);
        lastVisibleRow = Mathf.Clamp(lastVisibleRow, 0, m_TotalRow - 1);
    }

    /// <summary>
    /// 设置Item的位置
    /// </summary>
    protected void SetItemPosition(int rowIndex, int columnIndex, RectTransform rectTransform, float heightDelta, float widthDelta, float forceItemWidth)
    {
        //水平布局
        if (m_ExpandWidth)
        {
            SetChildAlongAxis(rectTransform, 0, padding.left + columnIndex * widthDelta, forceItemWidth);
        }
        else
        {
            SetChildAlongAxis(rectTransform, 0, padding.left + columnIndex * widthDelta);
        }

        //竖直布局
        SetChildAlongAxis(rectTransform, 1, padding.top + rowIndex * heightDelta);
    }

    #endregion
}